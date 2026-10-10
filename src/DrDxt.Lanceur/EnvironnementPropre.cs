namespace DrDxt.Lanceur;

/// <summary>
/// 10/10/2026 (revue de sécurité) : un processus élevé par l'UAC reçoit l'environnement de HKCU\Environment, que
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
        string? profilUtilisateur, string? appData, string? localAppData)
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
        return env;
    }

    /// <summary>Vrai pour une variable que le runtime .NET lit au démarrage : aucune ne doit sortir de Construire.</summary>
    public static bool EstUneVariableDuRuntime(string nom)
        => nom.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
        || nom.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
        || nom.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase)
        || nom.StartsWith("COR_", StringComparison.OrdinalIgnoreCase);
}
