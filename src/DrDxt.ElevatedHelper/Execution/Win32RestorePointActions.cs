using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32RestorePointActions — the real System Restore point (m2smoke boundary).
//
// 06/10/2026 (plans 4.5 et 4.5b) : le point se cree par l'outil de Windows, Checkpoint-Computer
// (CreerParCheckpoint). SRSetRestorePoint n'a cree aucun point sur 4 essais sur ma machine ; l'appel WMI
// direct depuis ce helper, 0 sur 2 ; Checkpoint-Computer, 3 sur 3. Le helper ne cree que sous le nom fixe
// de DrDXT, et retire ses plus anciens points, du plus ancien de la machine seulement (Volsnap efface dans
// l'ordre). Sources : learn.microsoft.com, SRSetRestorePointA et « Calling SRSetRestorePoint ». Un code non nul est un
// echec nomme ; zero ne prouve pas le point : SafetyManager relit la liste. Ne jette jamais.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class Win32RestorePointActions : IRestorePointActions
{
    private const uint APPLICATION_INSTALL = 0;
    private const uint BEGIN_SYSTEM_CHANGE = 100;

    // La création, injectable pour les tests : description -> (code de sortie, sortie d'erreur).
    private readonly Func<string, (uint Code, string? Erreur)> _creerParWmi;
    private readonly Func<IReadOnlyList<(uint Sequence, string Description)>> _lister;
    private readonly Func<uint, uint> _retirer;

    /// <summary>06/10/2026 (plan 4.5b, le mainteneur : « DrDXT garde actif max 2 points »).</summary>
    public const int PointsGardes = 2;

    public Win32RestorePointActions() : this(CreerParCheckpoint, ListerParWmi, SRRemoveRestorePoint) { }

    /// <summary>Tests : création seule, rien n'est listé ni retiré.</summary>
    internal Win32RestorePointActions(Func<string, (uint Code, string? Erreur)> creerParWmi) : this(creerParWmi, () => [], _ => 0) { }

    internal Win32RestorePointActions(Func<string, (uint Code, string? Erreur)> creerParWmi,
        Func<IReadOnlyList<(uint Sequence, string Description)>> lister, Func<uint, uint> retirer)
    {
        _creerParWmi = creerParWmi;
        _lister = lister;
        _retirer = retirer;
    }

    public Result CreatePoint(string description)
    {
        try
        {
            var (code, erreur) = _creerParWmi(Truncate(description));
            // Revue du 06/10 (P1) : powershell -Command sort 1 sur toute erreur ; la raison est la 1re ligne
            // de sa sortie d'erreur, et c'est elle que l'écran dit.
            var cree = code != 0 && PremiereLigne(erreur) is { } raison ? Fail(raison) : LireRetour(code, "Checkpoint-Computer");
            if (cree.IsOk) Elaguer();
            return cree;
        }
        catch (Exception ex)
        {
            return Fail($"Restore point creation failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>06/10/2026 (plan 4.5b) : le point est créé par l'outil de Windows lui-même. Décompilé
    /// (Microsoft.PowerShell.Commands.Management, CheckpointComputerCommand) : il appelle la même méthode WMI, puis son
    /// processus relit la liste jusqu'à voir le point (90 s). Lancé ainsi, il a réussi 3 fois sur 3 sur ma machine,
    /// une fois 18 s après un échec ; appelé depuis le helper lancé par l'App, le même appel WMI a échoué 2 fois sur 2
    /// (VSS 8193). Code de sortie 0 = accepté ; le point est relu par SafetyManager.</summary>
    private static string? PremiereLigne(string? texte)
    {
        var ligne = texte?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(ligne) ? null : ligne.Length <= 300 ? ligne : ligne[..300];
    }

    private static (uint Code, string? Erreur) CreerParCheckpoint(string description)
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in ArgumentsCheckpoint(description)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe non lancé");
        var sortie = p.StandardOutput.ReadToEndAsync();
        var erreurs = p.StandardError.ReadToEndAsync();
        // Checkpoint-Computer attend lui-même le point jusqu'à 90 s (décompilé) : 120 s laissent la marge.
        if (!p.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            p.Kill(entireProcessTree: true);
            return (0x800705B4, null); // ERROR_TIMEOUT
        }
        Task.WaitAll(sortie, erreurs);
        return (unchecked((uint)p.ExitCode), erreurs.Result);
    }

    /// <summary>Les arguments de Checkpoint-Computer : fixes ; le nom, entre apostrophes PowerShell échappées.</summary>
    internal static IReadOnlyList<string> ArgumentsCheckpoint(string description) =>
    [
        "-NoProfile", "-NonInteractive", "-Command",
        $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType APPLICATION_INSTALL -ErrorAction Stop",
    ];

    /// <summary>Les points de DrDXT à retirer pour n'en garder que <see cref="PointsGardes"/>. Pur.
    /// <para>Lu sur ma machine le 06/10/2026 (Volsnap, événement 95) : Windows n'efface les clichés que du plus ancien au
    /// plus récent ; retirer un point plus récent qu'un autre efface aussi l'autre. Retirer mes 9 points de test a ainsi
    /// effacé le point « Windows Update » plus ancien. On part donc du point le plus ancien de la machine, on retire tant
    /// que c'est un point de DrDXT (nom fixe exact ou ancien nom) et qu'il en reste trop, et on s'arrête au premier
    /// point d'un autre : mieux vaut un point DrDXT de trop que le point d'un autre perdu.</para></summary>
    internal static IReadOnlyList<uint> AElaguer(IEnumerable<(uint Sequence, string Description)> points)
    {
        var tries = points.OrderBy(p => p.Sequence).ToList();
        var restants = tries.Count(EstDrDxt);
        var retires = new List<uint>();
        foreach (var p in tries)
        {
            if (restants <= PointsGardes || !EstDrDxt(p)) break;
            retires.Add(p.Sequence);
            restants--;
        }
        return retires;

        static bool EstDrDxt((uint Sequence, string Description) p)
            => p.Description is CreateRestorePointOp.NomDrDxt or CreateRestorePointOp.AncienNom;
    }

    // Le ménage ne fait jamais échouer la création : le point neuf existe ; ce qui n'a pas été retiré le sera à la
    // prochaine création.
    private void Elaguer()
    {
        try
        {
            foreach (var sequence in AElaguer(_lister()))
                _retirer(sequence);
        }
        catch (Exception) { }
    }

    private static IReadOnlyList<(uint, string)> ListerParWmi()
    {
        var points = new List<(uint, string)>();
        using var s = new ManagementObjectSearcher(@"\\.\root\default", "SELECT SequenceNumber, Description FROM SystemRestore");
        foreach (var o in s.Get())
            using (o)
                if (o["SequenceNumber"] is { } n)
                    points.Add((Convert.ToUInt32(n, System.Globalization.CultureInfo.InvariantCulture), o["Description"]?.ToString() ?? ""));
        return points;
    }

    // ERROR_SUCCESS, ou ERROR_INVALID_DATA si le point n'existe plus (Microsoft Learn, SRRemoveRestorePoint).
    [System.Runtime.InteropServices.DllImport("srclient.dll")]
    private static extern uint SRRemoveRestorePoint(uint dwRPNum);

    /// <summary>Revue du 06/10 (P1) : le code des méthodes WMI de SystemRestore est un uint32, et un échec réel est
    /// un HRESULT (0x80070422 : service désactivé). Lu en int, il débordait (création) ou ne se lisait pas et passait
    /// pour un succès (activation). Absent ou illisible : échec, jamais succès.</summary>
    internal static Result LireRetour(object? retour, string methode)
    {
        if (retour is null || !uint.TryParse(retour.ToString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var code))
            return Fail($"{methode} : code de retour absent ou illisible.");
        return code == 0 ? Result.Ok() : Fail($"{methode} 0x{code:X8}");
    }

    // -------------------------------------------------------------------
    // EnableSystemRestore — the real "turn System Restore ON" (m2smoke boundary).
    //
    // Mirrors the proven qa/enable_sr.ps1 sequence in C#:
    //   1. Enable-ComputerRestore  → WMI root\default:SystemRestore.Enable(drive).
    //   2. vssadmin resize shadowstorage … /maxsize=5%  → give SR room to keep a
    //      point (a 0-byte allocation silently no-ops the create).
    // The drive is already allowlist-validated (system drive only). A failed Enable
    // is fatal (return Fail); the shadow-storage resize is best-effort (SR can still
    // create a point on a default allocation) so a resize hiccup does not block.
    // Never throws — every boundary is wrapped → Result.
    // -------------------------------------------------------------------
    // Drive-root form the WMI SystemRestore.Enable + vssadmin expect, e.g. "C:\".
    // 25/09/2026 : meme normalisation que OpAllowlist.NormalizeDriveLetter. L'allowlist validait la
    // lettre ROGNEE et l'executeur construisait la racine avec la valeur BRUTE : « C » donnait « C\ »,
    // « C: » avec espace donnait « C: \ ». Le valide et l'execute doivent etre la meme chaine.
    internal static string DriveRoot(string driveLetter) =>
        driveLetter.Trim().TrimEnd('\\', '/').TrimEnd(':').ToUpperInvariant() + ":\\";

    public Result EnableSystemRestore(string driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            return Fail("EnableSystemRestore: no drive letter.");

        try
        {
            var root = DriveRoot(driveLetter);

            var enabled = EnableComputerRestore(root);
            if (!enabled.IsOk)
                return enabled;

            // Best-effort: size the shadow storage to ~5% so a point can be kept. A
            // failure here is not fatal (SR may already have a usable allocation).
            // 07/10/2026 (revue F-13) : seulement quand aucune reserve n'existe. Une reserve deja fixee
            // (2 %, illimitee) est un choix de l'utilisateur ou de Windows : DrDXT ne l'ecrase plus, faute de pouvoir la rendre.
            if (DoitRedimensionner(LireReserves()))
                ResizeShadowStorage(root);

            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Fail($"EnableSystemRestore failed: {ex.GetType().Name}.");
        }
    }

    // Enable-ComputerRestore equivalent: WMI root\default:SystemRestore.Enable(drive).
    private static Result EnableComputerRestore(string driveRoot)
    {
        try
        {
            using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var inParams = cls.GetMethodParameters("Enable");
            inParams["Drive"] = driveRoot;
            using var outParams = cls.InvokeMethod("Enable", inParams, null);

            // ReturnValue 0 = success; anything else (incl. unsupported SKU / GPO) → Fail.
            return LireRetour(outParams?["ReturnValue"], "SystemRestore.Enable");
        }
        catch (Exception ex)
        {
            return Fail($"SystemRestore.Enable failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>07/10/2026 (F-13) : redimensionner seulement quand aucune reserve n'a de place. Non lu (null) = ne pas toucher.
    /// Limite connue : toutes les reserves de la machine, pas celle du seul disque systeme ; la jointure Win32_Volume si un
    /// second disque protege fausse la decision.</summary>
    internal static bool DoitRedimensionner(IReadOnlyList<ulong>? reserves) => reserves is not null && reserves.All(r => r == 0);

    private static IReadOnlyList<ulong>? LireReserves()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"\\.\root\cimv2", "SELECT MaxSpace FROM Win32_ShadowStorage");
            return s.Get().Cast<ManagementObject>().Select(o => Convert.ToUInt64(o["MaxSpace"] ?? 0UL)).ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // vssadmin resize shadowstorage /for=<drive> /on=<drive> /maxsize=5%.
    // Best-effort: a non-zero exit is swallowed (the caller treats Enable as the
    // gate; the resize only improves retention).
    private static void ResizeShadowStorage(string driveRoot)
    {
        try
        {
            // System32, pas le nom nu (ordre de recherche de CreateProcess, voir Win32ServiceActions).
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vssadmin.exe"),
                $"resize shadowstorage /for={driveRoot} /on={driveRoot} /maxsize=5%")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
        }
        catch
        {
            // Best-effort only — SR can still create a point on its default allocation.
        }
    }

    // La description d'un point : 256 caracteres au plus.
    private static string Truncate(string description)
    {
        const int max = 255;
        if (string.IsNullOrWhiteSpace(description))
            return "DrDXT";
        return description.Length <= max ? description : description[..max];
    }

    private static Result Fail(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);
}
