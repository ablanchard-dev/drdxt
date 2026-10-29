using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32RestorePointActions : le vrai point de Restauration du système (frontière du m2smoke).
//
// Le point se cree par Checkpoint-Computer (CreerParCheckpoint) : sur la machine de référence, SRSetRestorePoint 0 point
// sur 4 essais, l'appel WMI direct depuis ce helper 0 sur 2, Checkpoint-Computer 3 sur 3. Nom fixe de DrDXT seulement ;
// ses plus anciens points se retirent depuis le plus ancien de la machine (Volsnap efface dans l'ordre). Sources :
// learn.microsoft.com, SRSetRestorePointA et « Calling SRSetRestorePoint ». Un code non nul est un echec nomme ; zero ne
// prouve pas le point : SafetyManager relit la liste. Ne jette jamais.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class Win32RestorePointActions : IRestorePointActions
{
    // La création (Checkpoint-Computer, voir CreerParCheckpoint), injectable pour les tests : description -> (code de
    // sortie, sortie d'erreur).
    private readonly Func<string, (uint Code, string? Erreur)> _creer;
    private readonly Func<IReadOnlyList<(uint Sequence, string Description)>> _lister;
    private readonly Func<uint, uint> _retirer;

    /// <summary>Points de restauration de DrDXT gardés au plus ; les plus anciens sont retirés.</summary>
    public const int PointsGardes = 2;

    public Win32RestorePointActions() : this(CreerParCheckpoint, ListerParWmi, SRRemoveRestorePoint) { }

    /// <summary>Tests : création seule, rien n'est listé ni retiré.</summary>
    internal Win32RestorePointActions(Func<string, (uint Code, string? Erreur)> creerParWmi) : this(creerParWmi, () => [], _ => 0) { }

    internal Win32RestorePointActions(Func<string, (uint Code, string? Erreur)> creerParWmi,
        Func<IReadOnlyList<(uint Sequence, string Description)>> lister, Func<uint, uint> retirer)
    {
        _creer = creerParWmi;
        _lister = lister;
        _retirer = retirer;
    }

    public Result CreatePoint(string description)
    {
        try
        {
            var (code, erreur) = _creer(Truncate(description));
            // Powershell -Command sort 1 sur toute erreur ; l'écran dit la 1re ligne de sa sortie d'erreur.
            var cree = code != 0 && PremiereLigne(erreur) is { } raison ? Fail(raison) : LireRetour(code, "Checkpoint-Computer");
            if (cree.IsOk) Elaguer();
            return cree;
        }
        catch (Exception ex)
        {
            return Fail($"Restore point creation failed: {ex.GetType().Name}.");
        }
    }

    private static string? PremiereLigne(string? texte)
    {
        var ligne = texte?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(ligne) ? null : ligne.Length <= 300 ? ligne : ligne[..300];
    }

    /// <summary>Le point créé par l'outil de Windows. Décompilé (Microsoft.PowerShell.Commands.Management,
    /// CheckpointComputerCommand) : la même méthode WMI, puis la liste relue jusqu'au point (90 s). 3 sur 3 sur la machine
    /// de référence, une fois 18 s après un échec ; le même appel WMI depuis le helper : 2 échecs sur 2 (VSS 8193). Sortie
    /// 0 = accepté, le point est relu par SafetyManager.</summary>
    private static (uint Code, string? Erreur) CreerParCheckpoint(string description)
    {
        var psi = InfosCheckpoint(description, Environment.GetFolderPath(Environment.SpecialFolder.System));
        // Checkpoint-Computer attend lui-même le point jusqu'à 90 s (décompilé) : 120 s laissent la marge.
        var r = ProcessusBorne.Executer(psi, TimeSpan.FromSeconds(120));
        if (!r.Lance) throw new InvalidOperationException("powershell.exe not started");
        if (!r.Fini) return (0x800705B4, null); // ERROR_TIMEOUT
        return (unchecked((uint)r.Code), r.Erreurs);
    }

    /// <summary>Le PowerShell qui crée le point. PowerShell 5.1 met <c>Documents\WindowsPowerShell\Modules</c> en tête de
    /// PSModulePath, même élevé, où l'utilisateur écrit sans droits : un Checkpoint-Computer posé là tournerait en
    /// administrateur. PSModulePath ne garde donc que les modules de Windows (plus Program Files, réservé aux
    /// administrateurs), le module est importé par son chemin, et le script s'arrête si la commande vient d'ailleurs.</summary>
    internal static ProcessStartInfo InfosCheckpoint(string description, string system32)
    {
        var psi = new ProcessStartInfo(Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["PSModulePath"] = ModulesDeWindows(system32);
        // powershell.exe est .NET Framework : COR_PROFILER et ses voisines y chargeraient une DLL. Retirées ici aussi, pour
        // un helper lancé hors du lanceur (outils de banc).
        RetirerLeRuntime(psi.Environment);
        foreach (var a in ArgumentsCheckpoint(description, system32)) psi.ArgumentList.Add(a);
        return psi;
    }

    internal static void RetirerLeRuntime(IDictionary<string, string?> environnement)
    {
        foreach (var nom in environnement.Keys.Where(VariableDuRuntime).ToList()) environnement.Remove(nom);
    }

    /// <summary>Même liste que celle du lanceur (EnvironnementPropre.EstUneVariableDuRuntime).</summary>
    internal static bool VariableDuRuntime(string nom)
        => nom.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
        || nom.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
        || nom.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase)
        || nom.StartsWith("COR_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Les modules livrés avec Windows ($PSHOME\Modules), par le chemin tiré de l'API.</summary>
    internal static string ModulesDeWindows(string system32) => Path.Combine(system32, @"WindowsPowerShell\v1.0\Modules");

    /// <summary>Le module qui porte Checkpoint-Computer, importé par son chemin ; sortie 3 si la commande n'en vient pas.</summary>
    internal static string ModuleSysteme(string system32) =>
        $"Import-Module -Name '{ModulesDeWindows(system32)}\\Microsoft.PowerShell.Management\\Microsoft.PowerShell.Management.psd1' -ErrorAction Stop;" +
        "if (-not (Get-Command Checkpoint-Computer).Module.Path.StartsWith(" +
        $"'{system32}\\', [StringComparison]::OrdinalIgnoreCase)) {{ exit 3 }};";

    /// <summary>Les arguments de Checkpoint-Computer : fixes ; le nom, entre apostrophes PowerShell échappées.</summary>
    internal static IReadOnlyList<string> ArgumentsCheckpoint(string description, string system32) =>
    [
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
        ModuleSysteme(system32)
        + $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType APPLICATION_INSTALL -ErrorAction Stop",
    ];

    /// <summary>Les points de DrDXT à retirer pour n'en garder que <see cref="PointsGardes"/>. Pur.
    /// <para>Lu le 06/10/2026 sur la machine de référence (Volsnap, événement 95) : Windows efface les clichés du plus ancien au plus
    /// récent, et retirer un point plus récent efface aussi les plus anciens (9 points de test retirés ont effacé un point
    /// « Windows Update »). Depuis le plus ancien de la machine : on retire tant que c'est un point de DrDXT (nom fixe ou
    /// ancien nom) et qu'il en reste trop, arrêt au premier point d'un autre.</para></summary>
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

    // Le ménage ne fait jamais échouer la création ; le reste se retire à la prochaine.
    private void Elaguer()
    {
        try
        {
            foreach (var sequence in AElaguer(_lister()))
                _retirer(sequence);
        }
        catch (Exception) { /* ménage remis à la prochaine création */ }
    }

    private static IReadOnlyList<(uint, string)> ListerParWmi() => LectureWmiBornee.Lire<IReadOnlyList<(uint, string)>>(() =>
    {
        var points = new List<(uint, string)>();
        using var s = new ManagementObjectSearcher(@"\\.\root\default", "SELECT SequenceNumber, Description FROM SystemRestore");
        using var rangees = s.Get();
        foreach (var o in rangees)
            using (o)
                if (o["SequenceNumber"] is { } n)
                    points.Add((Convert.ToUInt32(n, System.Globalization.CultureInfo.InvariantCulture), o["Description"]?.ToString() ?? ""));
        return points;
    }, () => throw new TimeoutException("WMI did not answer in time."));

    // ERROR_SUCCESS, ou ERROR_INVALID_DATA si le point n'existe plus (Microsoft Learn, SRRemoveRestorePoint).
    [System.Runtime.InteropServices.DllImport("srclient.dll")]
    private static extern uint SRRemoveRestorePoint(uint dwRPNum);

    /// <summary>Le code des méthodes WMI de SystemRestore est un uint32, un échec réel un HRESULT (0x80070422 : service
    /// désactivé) : lu en int, il déborderait ou passerait pour un succès. Absent ou illisible : échec.</summary>
    internal static Result LireRetour(object? retour, string methode)
    {
        if (retour is null || !uint.TryParse(retour.ToString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var code))
            return Fail($"{methode} : code de retour absent ou illisible.");
        return code == 0 ? Result.Ok() : Fail($"{methode} 0x{code:X8}");
    }

    // -------------------------------------------------------------------
    // EnableSystemRestore : le vrai « activer la Restauration du système » (frontière du m2smoke), la séquence de
    // qa/enable_sr.ps1 :
    //   1. Enable-ComputerRestore → WMI root\default:SystemRestore.Enable(drive), fatal s'il rate ;
    //   2. vssadmin resize shadowstorage … /maxsize=5%, au mieux (une allocation de 0 octet rend la création sans effet).
    // Lecteur système seulement, validé par l'allowlist. Ne lève jamais.
    // -------------------------------------------------------------------
    // La racine de lecteur (« C:\ ») pour SystemRestore.Enable et vssadmin, normalisée comme
    // OpAllowlist.NormalizeDriveLetter : la valeur brute donnerait « C\ » ou « C: \ ». Validé et exécuté = même chaîne.
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

            // Au mieux, et seulement sans aucune reserve : ~5 % de shadow storage. Une reserve deja fixee (2 %, illimitee)
            // est un choix de l'utilisateur ou de Windows, que DrDXT ne saurait rendre.
            if (DoitRedimensionner(LireReserves()))
                ResizeShadowStorage(root);

            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Fail($"EnableSystemRestore failed: {ex.GetType().Name}.");
        }
    }

    // Équivalent d'Enable-ComputerRestore : WMI root\default:SystemRestore.Enable(drive).
    private static Result EnableComputerRestore(string driveRoot)
    {
        try
        {
            using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var inParams = cls.GetMethodParameters("Enable");
            inParams["Drive"] = driveRoot;
            using var outParams = cls.InvokeMethod("Enable", inParams, null);

            // ReturnValue 0 = succès ; tout le reste (y compris SKU non pris en charge / GPO) → Fail.
            return LireRetour(outParams?["ReturnValue"], "SystemRestore.Enable");
        }
        catch (Exception ex)
        {
            return Fail($"SystemRestore.Enable failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>Redimensionner seulement quand aucune reserve n'a de place. Non lu (null) = ne pas toucher.
    /// Limite connue : toutes les reserves de la machine, pas celle du seul disque systeme ; la jointure Win32_Volume si un
    /// second disque protege fausse la decision.</summary>
    internal static bool DoitRedimensionner(IReadOnlyList<ulong>? reserves) => reserves is not null && reserves.All(r => r == 0);

    private static IReadOnlyList<ulong>? LireReserves() => LectureWmiBornee.Lire<IReadOnlyList<ulong>?>(() =>
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"\\.\root\cimv2", "SELECT MaxSpace FROM Win32_ShadowStorage");
            using var rangees = s.Get();
            return rangees.Cast<ManagementObject>().Select(o => { using (o) return Convert.ToUInt64(o["MaxSpace"] ?? 0UL); }).ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }, () => null);

    // vssadmin resize shadowstorage /for=<drive> /on=<drive> /maxsize=5%, au mieux : une sortie non nulle est avalée.
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
            ProcessusBorne.Executer(psi, TimeSpan.FromSeconds(15));
        }
        catch
        {
            // Au mieux seulement : la restauration peut encore créer un point sur son allocation par défaut.
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
