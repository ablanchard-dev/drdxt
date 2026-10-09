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
/// notre table — un code non repertorie reste un chiffre, jamais un nom invente.</param>
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
    /// L'incident vient-il du sous-systeme graphique ?
    ///
    /// La question decide si une experience GPU peut avoir lieu, donc elle se tranche sur les
    /// codes eux-memes et non sur le bucket : le bucket `WATCHDOG` heberge aussi des gardiens qui
    /// n'ont rien de graphique. Les cinq codes retenus sont exactement ceux dont le nom SDK designe
    /// le noyau graphique (`VIDEO_*`, `*_DXGKRNL_*`).
    ///
    /// Un code que nous ne savons pas nommer rend <c>false</c> : il pourrait etre graphique, et
    /// nous l'ignorons. Cette porte retient la main, elle ne la force pas — se tromper vers
    /// « inconnu donc pas graphique » coute une experience potentiellement risquee, se tromper vers
    /// l'inverse gelerait la carte sur un incident sans rapport.
    /// </summary>
    public bool EstGraphique => Code is 0x113 or 0x116 or 0x117 or 0x141 or 0x193;

    public string Phrase => NomSdk is { } n
        ? $"0x{Code:X} {n}" + (Parametre1 is { } p ? $", paramètre 1 : 0x{p}" : "")
        : $"0x{Code:X} (code non repertorie)";
}

/// <summary>
/// Les incidents noyau que la machine a CONSERVES, meme quand elle n'a pas plante.
///
/// Un incident live n'est pas un plantage : le noyau a pris une photo et la machine a continue
/// de tourner. Les additionner aux arrets brutaux de <see cref="IUnexpectedShutdownLog"/>
/// compterait ensemble deux natures d'incident differentes.
/// </summary>
public interface ILiveKernelReportLog
{
    /// <summary>Les rapports, plus recents d'abord. Une liste vide est une reponse ; un echec de
    /// lecture est un `Fail`, jamais une liste vide.</summary>
    Result<IReadOnlyList<LiveKernelReport>> Lire();

    /// <summary>Revue (F-31) : les rapports présents mais refusés à la dernière lecture, quand d'autres ont été lus.
    /// Ils sont dits à part ; les lus restent utilisés (point sain, rapprochement avec les installations).</summary>
    int Refuses => 0;
}

/// <summary>
/// La quatrieme classe de preuve, et la seule qui restait sur cette machine.
///
/// Constat du 08/09/2026 sur la machine de reference. Trois sources de preuve de plantage
/// etaient soit vides, soit en echec :
///
///   `C:\WINDOWS\MEMORY.DMP`            absent
///   `C:\WINDOWS\Minidump\`             vide
///   `volmgr` 161, 02/09 05:09 et 04/09 16:44
///                                      « la creation du fichier de vidage a echoue »,
///                                      `BugCheckProgress = 0x00040049` les deux fois
///
/// Et ce n'est ni la faute du fichier d'echange (33 Go, gere par le systeme, sur le volume de
/// demarrage, pic a 1,7 Go) ni de la place disque (291 Go libres). Consequence a retenir : le
/// « reparage » evident — remettre `CrashDumpEnabled` a 7 au lieu de 3 — n'aurait rien repare,
/// puisque c'est la CREATION du vidage qui echoue, pas son type.
///
/// Pourtant la machine avait garde quatre rapports, que rien ne lisait :
///
///   USBXHCI-20260222-0042.dmp   0x144 BUGCODE_USB3_DRIVER        22/02/2026 00:42:17
///   WATCHDOG-20260815-0138.dmp  0x193 VIDEO_DXGKRNL_LIVEDUMP     15/08/2026 01:38:49
///   WATCHDOG-20260818-1942.dmp  0x193 VIDEO_DXGKRNL_LIVEDUMP     18/08/2026 19:42:09
///   WATCHDOG-20260822-0234.dmp  0x193 VIDEO_DXGKRNL_LIVEDUMP     22/08/2026 02:34:32
///
/// Trois gardiens du noyau graphique en huit jours, code identique, parametre 1 identique
/// (`0x80e`). Ce n'est pas du bruit, et ca conditionne toute experimentation GPU.
///
/// ══ pourquoi la lecture se fait par index ══
/// `Report.wer` est LOCALISE : sur ce Windows francais les champs s'appellent
/// `Sig[1].Name=Paramètre 1`. Chercher « Parameter 1 » ne trouve rien ici, et coder « Paramètre »
/// ne trouverait rien ailleurs. On lit donc les `Sig[N].Value` par INDICE, apres avoir verifie
/// `EventType=LiveKernelEvent` — c'est cet en-tete qui garantit le schema. Meme lecon que les
/// compteurs de performance.
///
/// Non couvert, et dit plutot que devine : `C:\Windows\LiveKernelReports` est l'autre
/// emplacement de ces vidages. Il est vide sur cette machine, donc invérifiable ici — ecrire son
/// analyse serait livrer du code que rien n'a prouve.
/// </summary>
public sealed class WerLiveKernelReportLog : ILiveKernelReportLog
{
    /// <summary>
    /// Les codes que nous savons nommer, releves dans
    /// `Windows Kits\10\Include\10.0.22621.0\shared\bugcodes.h` le 08/09/2026 — pas de memoire,
    /// pas de blog. Une table courte et exacte vaut mieux qu'une longue et approximative : un code
    /// absent d'ici ressort en chiffre, ce qui est vrai, plutot qu'en nom plausible, qui serait
    /// faux.
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

    /// <summary>Le nom SDK d'un code d'arret, ou <c>null</c> s'il n'est pas dans la table. 17/09/2026 :
    /// la table servait aux incidents a chaud ; les ecrans bleus du bilan n'affichaient que le code.</summary>
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
        // La file et l'archive : Windows deplace les rapports de l'une a l'autre apres envoi, et
        // ne lire que la file perdrait tout l'historique d'une machine qui a rapporte ses erreurs.
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
                }
            }

            // F-31 : un refus partiel garde ce qui a été lu ; seul « tout refusé » est un échec. F-36 : la cause (droits ou
            // dossier durci) est dite par l'appelant, qui sait s'il tourne en administrateur.
            Refuses = refuses;
            if (refuses > 0 && rapports.Count == 0)
                return Result<IReadOnlyList<LiveKernelReport>>.Fail(ErrorCodes.DetectionFailed,
                    $"{refuses} rapport(s) d'incident noyau existent mais n'ont pas pu être lus (accès refusé). "
                    + "« Non lu » n'est pas « aucun incident ».");

            rapports.Sort((a, b) => b.Quand.CompareTo(a.Quand));
            return Result<IReadOnlyList<LiveKernelReport>>.Ok(rapports);
        }
        catch (Exception ex)
        {
            // Acces refuse sur une machine durcie, dossier verrouille : on le DIT. Rendre une
            // liste vide ferait passer « je n'ai pas pu lire » pour « aucun incident ».
            return Result<IReadOnlyList<LiveKernelReport>>.Fail(
                ErrorCodes.DetectionFailed, "Rapports WER illisibles : " + ex.Message);
        }
    }

    private static LiveKernelReport? Analyser(string dossier)
    {
        var wer = Path.Combine(dossier, "Report.wer");
        // 06/10/2026 (chantier B8c) : pas de `File.Exists`. Sans droits administrateur, chaque dossier `Kernel_*`
        // est fermé et `File.Exists` y répond « absent » : un refus passait pour une absence. On ouvre ; un refus lève
        // UnauthorizedAccessException, compté par Lire().
        var champs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // `Report.wer` est en UTF-16. On laisse la marque d'ordre des octets decider quand elle
        // est presente, faute de quoi un fichier UTF-8 ressortirait en caracteres nuls.
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
            || !long.TryParse(brut, out var filetime)) return null;

        // HEXADECIMAL, et ce n'est pas un pari. WER ecrit ces valeurs sans prefixe : `144`,
        // `193`, mais aussi `80e` et `ffff920c4059c760`, qui ne peuvent etre que de l'hexadecimal.
        // Deux verifications independantes le confirment sur le code lui-meme : `144` accompagne un
        // vidage nomme `USBXHCI` (0x144 = BUGCODE_USB3_DRIVER) et `193` un vidage du noyau
        // graphique (0x193 = VIDEO_DXGKRNL_LIVEDUMP). Lus en decimal ils donneraient 0x90 et 0xC1,
        // qui ne designent ni l'USB ni le graphique.
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
