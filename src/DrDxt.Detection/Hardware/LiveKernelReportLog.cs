using System.Globalization;
using System.Text;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Un rapport noyau conserve par Windows Error Reporting.
/// </summary>
/// <param name="Quand">L'instant de l'incident, en UTC.</param>
/// <param name="Code">Le code de verification de bogue, tel qu'ecrit par WER.</param>
/// <param name="NomSdk">Le nom du code selon `bugcodes.h`. <c>null</c> quand le code n'est pas dans
/// notre table : un code non repertorie reste un chiffre, jamais un nom invente.</param>
/// <param name="Bucket">Le classement de Windows, lu sur le nom du vidage (`WATCHDOG`,
/// `USBXHCI`…). <c>null</c> quand aucun vidage n'est conserve.</param>
/// <param name="Parametre1">Le premier parametre du code, en hexadecimal. C'est lui qui distingue
/// deux incidents de meme code.</param>
public sealed record LiveKernelReport(
    DateTimeOffset Quand,
    uint Code,
    string? NomSdk,
    string? Bucket,
    string? Parametre1)
{
    /// <summary>
    /// L'incident vient-il du sous-systeme graphique ? Elle decide si une experience GPU peut avoir lieu, donc se tranche
    /// sur les codes, pas sur le bucket (`WATCHDOG` heberge aussi des gardiens non graphiques) : les cinq codes dont le
    /// nom SDK designe le noyau graphique (`VIDEO_*`, `*_DXGKRNL_*`). Un code que nous ne savons pas nommer rend
    /// <c>false</c> : l'inverse gelerait la carte sur un incident sans rapport.
    /// </summary>
    public bool EstGraphique => CodeGraphique(Code);

    /// <summary>La même liste, pour qui n'a que le code (l'écran le relit dans la valeur du constat).</summary>
    public static bool CodeGraphique(uint code) => code is 0x113 or 0x116 or 0x117 or 0x141 or 0x193;

    public string Phrase => NomSdk is { } n
        ? $"0x{Code:X} {n}" + (Parametre1 is { } p ? Docteur.T("LK.Parametre1", $"0x{p}") : "")
        : Docteur.T("LK.CodeInconnu", $"0x{Code:X}");
}

/// <summary>
/// Les incidents noyau que la machine a conserves, meme sans planter : le noyau a pris une photo et la machine a
/// continue. Ils ne s'additionnent pas aux arrets brutaux de <see cref="IUnexpectedShutdownLog"/>.
/// </summary>
public interface ILiveKernelReportLog
{
    /// <summary>Les rapports, plus recents d'abord. Une liste vide est une reponse ; un echec de
    /// lecture est un `Fail`, jamais une liste vide.</summary>
    Result<IReadOnlyList<LiveKernelReport>> Lire();

    /// <summary>Les rapports présents mais refusés à la dernière lecture, quand d'autres ont été lus.
    /// Ils sont dits à part ; les lus restent utilisés (point sain, rapprochement avec les installations).</summary>
    int Refuses => 0;
}

/// <summary>
/// La quatrieme classe de preuve. Constat du 08/09/2026 sur la machine de reference, les trois autres sources :
///   `C:\WINDOWS\MEMORY.DMP`            absent
///   `C:\WINDOWS\Minidump\`             vide
///   `volmgr` 161, 02/09 05:09 et 04/09 16:44
///                                      « la creation du fichier de vidage a echoue »,
///                                      `BugCheckProgress = 0x00040049` les deux fois
/// Ni le fichier d'echange (33 Go, gere par le systeme, sur le volume de demarrage, pic a 1,7 Go) ni la place disque
/// (291 Go libres) : remettre `CrashDumpEnabled` a 7 au lieu de 3 n'aurait rien repare, c'est la creation qui echoue.
/// La machine avait pourtant garde quatre rapports :
///   USBXHCI-20260222-0042.dmp   0x144 BUGCODE_USB3_DRIVER        22/02/2026 00:42:17
///   WATCHDOG-20260815-0138.dmp  0x193 VIDEO_DXGKRNL_LIVEDUMP     15/08/2026 01:38:49
///   WATCHDOG-20260818-1942.dmp  0x193 VIDEO_DXGKRNL_LIVEDUMP     18/08/2026 19:42:09
///   WATCHDOG-20260822-0234.dmp  0x193 VIDEO_DXGKRNL_LIVEDUMP     22/08/2026 02:34:32
/// Trois gardiens du noyau graphique en huit jours, meme code, meme parametre 1 (`0x80e`) : ca conditionne toute
/// experimentation GPU.
///
/// Lecture par indice : `Report.wer` est localise (`Sig[1].Name=Paramètre 1` sur ce Windows francais), donc on lit
/// les `Sig[N].Value` par position apres avoir verifie `EventType=LiveKernelEvent`, l'en-tete qui garantit le schema.
///
/// Non couvert : `C:\Windows\LiveKernelReports`, l'autre emplacement, vide sur cette machine donc invérifiable ici.
/// </summary>
public sealed class WerLiveKernelReportLog : ILiveKernelReportLog
{
    /// <summary>
    /// Les codes que nous savons nommer, releves le 08/09/2026 dans
    /// `Windows Kits\10\Include\10.0.22621.0\shared\bugcodes.h` (source : le SDK, ni memoire ni blog). Un code absent
    /// d'ici ressort en chiffre, jamais en nom plausible.
    /// </summary>
    private static readonly IReadOnlyDictionary<uint, string> NomsSdk = new Dictionary<uint, string>
    {
        [0x0000000A] = "IRQL_NOT_LESS_OR_EQUAL",
        [0x0000001A] = "MEMORY_MANAGEMENT",
        [0x0000003B] = "SYSTEM_SERVICE_EXCEPTION",
        [0x0000009C] = "MACHINE_CHECK_EXCEPTION",
        [0x00000101] = "CLOCK_WATCHDOG_TIMEOUT",
        [0x00000113] = "VIDEO_DXGKRNL_FATAL_ERROR",
        [0x00000116] = "VIDEO_TDR_FAILURE",
        [0x00000117] = "VIDEO_TDR_TIMEOUT_DETECTED",
        [0x00000122] = "WHEA_INTERNAL_ERROR",
        [0x00000124] = "WHEA_UNCORRECTABLE_ERROR",
        [0x00000133] = "DPC_WATCHDOG_VIOLATION",
        [0x00000139] = "KERNEL_SECURITY_CHECK_FAILURE",
        [0x00000141] = "VIDEO_ENGINE_TIMEOUT_DETECTED",
        [0x00000144] = "BUGCODE_USB3_DRIVER",
        [0x00000154] = "UNEXPECTED_STORE_EXCEPTION",
        [0x00000161] = "LIVE_SYSTEM_DUMP",
        [0x00000193] = "VIDEO_DXGKRNL_LIVEDUMP",
        [0x000000EF] = "CRITICAL_PROCESS_DIED",
    };

    /// <summary>Le nom SDK d'un code d'arret, ou <c>null</c> s'il n'est pas dans la table (aussi pour les ecrans bleus
    /// du bilan).</summary>
    public static string? NomSdk(int code) => NomsSdk.TryGetValue(unchecked((uint)code), out var n) ? n : null;

    private readonly string[] _racines;

    public int Refuses { get; private set; }

    public WerLiveKernelReportLog(params string[] racines)
    {
        _racines = racines.Length > 0 ? racines : DossiersParDefaut();
    }

    private static string[] DossiersParDefaut()
    {
        var wer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "WER");
        // La file et l'archive : Windows deplace les rapports de l'une a l'autre apres envoi.
        return [Path.Combine(wer, "ReportQueue"), Path.Combine(wer, "ReportArchive")];
    }

    public Result<IReadOnlyList<LiveKernelReport>> Lire()
    {
        try
        {
            var rapports = new List<LiveKernelReport>();
            var refuses = 0;

            foreach (var racine in _racines)
            {
                if (!Directory.Exists(racine)) continue;

                foreach (var dossier in Directory.EnumerateDirectories(racine, "Kernel_*"))
                {
                    try
                    {
                        if (Analyser(dossier) is { } r) rapports.Add(r);
                    }
                    catch (UnauthorizedAccessException) { refuses++; }
                    // WER peut tenir le rapport ouvert : ce dossier seul n'est pas lu, les autres restent.
                    catch (IOException) { }
                }
            }

            // Un refus partiel garde ce qui a été lu ; seul « tout refusé » est un échec. La cause (droits ou
            // dossier durci) est dite par l'appelant, qui sait s'il tourne en administrateur.
            Refuses = refuses;
            if (refuses > 0 && rapports.Count == 0)
                return Result<IReadOnlyList<LiveKernelReport>>.Fail(ErrorCodes.DetectionFailed,
                    Docteur.T("LK.Refuses", refuses));

            rapports.Sort((a, b) => b.Quand.CompareTo(a.Quand));
            return Result<IReadOnlyList<LiveKernelReport>>.Ok(rapports);
        }
        catch (Exception ex)
        {
            // Acces refuse, dossier verrouille : on le dit ; une liste vide ferait passer « pas pu lire » pour « aucun incident ».
            return Result<IReadOnlyList<LiveKernelReport>>.Fail(
                ErrorCodes.DetectionFailed, "Rapports WER illisibles : " + ex.Message);
        }
    }

    private static LiveKernelReport? Analyser(string dossier)
    {
        var wer = Path.Combine(dossier, "Report.wer");
        // Pas de `File.Exists` : sans administrateur, chaque dossier `Kernel_*` est fermé et il y répond « absent ». On
        // ouvre ; un refus lève UnauthorizedAccessException, compté par Lire().
        var champs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // `Report.wer` est en UTF-16 ; la marque d'ordre des octets decide quand elle est la (un UTF-8 sortirait en nuls).
        StreamReader ouvert;
        try { ouvert = new StreamReader(wer, Encoding.Unicode, detectEncodingFromByteOrderMarks: true); }
        catch (FileNotFoundException) { return null; }
        using (var lecteur = ouvert)
        {
            while (lecteur.ReadLine() is { } ligne)
            {
                var egal = ligne.IndexOf('=');
                if (egal > 0) champs[ligne[..egal]] = ligne[(egal + 1)..];
            }
        }

        // L'en-tete garantit le schema des `Sig[N]` : sans lui, l'indice 0 ne veut rien dire.
        if (!champs.TryGetValue("EventType", out var type) || type != "LiveKernelEvent") return null;

        if (!champs.TryGetValue("EventTime", out var brut)
            || !long.TryParse(brut, out var filetime)
            // Hors de la plage de Windows, FromFileTimeUtc lève : ce rapport-là est abîmé, pas les autres.
            || filetime < 0 || filetime > DateTime.MaxValue.ToFileTimeUtc()) return null;

        // Hexadecimal : WER ecrit sans prefixe `144`, `193`, mais aussi `80e` et `ffff920c4059c760`. Verifie sur le code :
        // `144` accompagne un vidage `USBXHCI` (0x144 = BUGCODE_USB3_DRIVER), `193` un vidage graphique
        // (0x193 = VIDEO_DXGKRNL_LIVEDUMP) ; en decimal, 0x90 et 0xC1 ne designeraient ni l'un ni l'autre.
        if (!champs.TryGetValue("Sig[0].Value", out var codeBrut)
            || !uint.TryParse(codeBrut, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            return null;

        var vidage = Directory.EnumerateFiles(dossier, "*.dmp").FirstOrDefault();

        return new LiveKernelReport(
            new DateTimeOffset(DateTime.FromFileTimeUtc(filetime), TimeSpan.Zero),
            code,
            NomsSdk.GetValueOrDefault(code),
            vidage is null ? null : Path.GetFileNameWithoutExtension(vidage).Split('-')[0],
            champs.GetValueOrDefault("Sig[1].Value"));
    }
}
