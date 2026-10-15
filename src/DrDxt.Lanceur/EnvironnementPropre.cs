using System.IO;

namespace DrDxt.Lanceur;

/// <summary>
/// Un processus élevé par l'UAC reçoit l'environnement de HKCU\Environment, que
/// n'importe quel programme du compte écrit sans droits. Le runtime .NET y lit, avant la première ligne du programme,
/// de quoi charger une DLL de son choix (CORECLR_ENABLE_PROFILING, CORECLR_PROFILER_PATH, documentées par Microsoft)
/// : chaque « Oui » donné à DrDXT aurait fait tourner ce code en administrateur. Le lanceur ne transmet RIEN de
/// l'environnement reçu : il reconstruit le minimum depuis des valeurs lues aux API Windows.
///
/// Ce fichier est aussi compilé dans les tests du helper (lien dans le csproj), le lanceur natif n'étant pas
/// référençable.
/// </summary>
public static class EnvironnementPropre
{
    /// <summary>Les seuls programmes que le lanceur démarre, à côté de lui, jamais ailleurs.</summary>
    public static readonly string[] Executables = ["DrDxt.ElevatedHelper.exe", "DrDxt.App.exe", "DrDXT-Test.exe"];

    /// <summary>Le PATH ne contient que Windows : un programme élevé ne cherche pas ses DLL dans un dossier du compte.</summary>
    public static IReadOnlyDictionary<string, string> Construire(string dossierWindows, string dossierTemp,
        string? profilUtilisateur, string? appData, string? localAppData, DossiersDuSysteme? systeme = null)
    {
        var w = dossierWindows.TrimEnd('\\');
        var lecteur = Path.GetPathRoot(w)?.TrimEnd('\\') ?? "C:";
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = w,
            ["windir"] = w,
            ["SystemDrive"] = lecteur,
            ["ComSpec"] = Path.Combine(w, "System32", "cmd.exe"),
            ["PATH"] = string.Join(';', Path.Combine(w, "System32"), w, Path.Combine(w, "System32", "Wbem"),
                Path.Combine(w, "System32", "WindowsPowerShell", "v1.0")),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["OS"] = "Windows_NT",
            ["TEMP"] = dossierTemp,
            ["TMP"] = dossierTemp,
        };
        // Lus aux API (dossiers connus du jeton), jamais à l'environnement reçu : l'App élevée range ses relevés
        // dans le profil du joueur.
        if (!string.IsNullOrEmpty(profilUtilisateur)) env["USERPROFILE"] = profilUtilisateur;
        if (!string.IsNullOrEmpty(appData)) env["APPDATA"] = appData;
        if (!string.IsNullOrEmpty(localAppData)) env["LOCALAPPDATA"] = localAppData;
        // Sans ProgramFiles ni ProgramData, l'audit de santé de l'App élevée laissait
        // « %ProgramFiles%\... » tel quel dans les chemins lus au registre, donc des tâches et services passaient en
        // silence : le scan élevé auditait MOINS que le scan simple. Tout vient des API (dossiers connus), rien de reçu.
        if (systeme is { } s)
        {
            if (!string.IsNullOrEmpty(s.ProgramFiles)) { env["ProgramFiles"] = s.ProgramFiles; env["ProgramW6432"] = s.ProgramFiles; }
            if (!string.IsNullOrEmpty(s.ProgramFilesX86)) env["ProgramFiles(x86)"] = s.ProgramFilesX86;
            if (!string.IsNullOrEmpty(s.CommonProgramFiles)) { env["CommonProgramFiles"] = s.CommonProgramFiles; env["CommonProgramW6432"] = s.CommonProgramFiles; }
            if (!string.IsNullOrEmpty(s.CommonProgramFilesX86)) env["CommonProgramFiles(x86)"] = s.CommonProgramFilesX86;
            if (!string.IsNullOrEmpty(s.ProgramData)) { env["ProgramData"] = s.ProgramData; env["ALLUSERSPROFILE"] = s.ProgramData; }
            if (!string.IsNullOrEmpty(s.Public)) env["PUBLIC"] = s.Public;
            if (!string.IsNullOrEmpty(s.NomUtilisateur)) env["USERNAME"] = s.NomUtilisateur;
            if (s.Processeurs > 0) env["NUMBER_OF_PROCESSORS"] = s.Processeurs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(s.Architecture)) env["PROCESSOR_ARCHITECTURE"] = s.Architecture;
        }
        return env;
    }

    /// <summary>Les dossiers du système que l'audit de santé lit par leurs variables ; lus aux API par le lanceur.</summary>
    public sealed record DossiersDuSysteme(string? ProgramFiles, string? ProgramFilesX86, string? CommonProgramFiles,
        string? CommonProgramFilesX86, string? ProgramData, string? Public, string? NomUtilisateur, int Processeurs, string? Architecture);

    /// <summary>Le mot qui demande au lanceur la console de réparation de Windows (DISM puis sfc), au lieu d'un programme.</summary>
    public const string ReparerWindows = "reparer-windows";

    /// <summary>La console de réparation est un PowerShell élevé, donc .NET Framework, qui
    /// lit lui aussi de quoi charger une DLL dans l'environnement (COR_PROFILER). Elle passe par le lanceur, avec cette
    /// commande FIXE : chemins absolus de System32, sans profil, rien qui vienne de l'appelant.</summary>
    public static (string Fichier, string Arguments) ConsoleDeReparation(string system32) => (
        Path.Combine(system32, @"WindowsPowerShell\v1.0\powershell.exe"),
        $"-NoProfile -NoExit -Command \"& '{Path.Combine(system32, "Dism.exe")}' /Online /Cleanup-Image /RestoreHealth; "
        + $"& '{Path.Combine(system32, "sfc.exe")}' /scannow; "
        + $"& '{Path.Combine(system32, "Dism.exe")}' /Online /Cleanup-Image /CheckHealth\"");

    /// <summary>Vrai pour une variable que le runtime .NET lit au démarrage : aucune ne doit sortir de Construire.</summary>
    public static bool EstUneVariableDuRuntime(string nom)
        => nom.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
        || nom.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
        || nom.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase)
        || nom.StartsWith("COR_", StringComparison.OrdinalIgnoreCase);
}
