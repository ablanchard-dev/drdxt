using System.Diagnostics;
using System.IO;

namespace DrDxt.App.Platform;

/// <summary>
/// 10/10/2026 (revue de sécurité) : l'App ne se relance plus élevée directement. Un processus élevé par l'UAC hérite
/// de HKCU\Environment, écrivable par tout programme du compte, et le runtime .NET y lit de quoi charger une DLL avant
/// la première ligne (CORECLR_PROFILER_PATH). L'invite Windows porte le lanceur natif (DrDxt.Lanceur.exe), qui
/// redémarre l'App avec un environnement reconstruit. Sans lanceur à côté (build de développement), relance directe.
/// </summary>
public static class RelanceElevee
{
    public const string Lanceur = "DrDxt.Lanceur.exe";
    private const int ElevationRefusee = 1223;

    /// <summary>Faux quand l'invite Windows est refusée ou que rien ne démarre : l'App reste ouverte et le dit.</summary>
    public static bool Demarrer(params string[] arguments)
    {
        if (Environment.ProcessPath is not { } exe) return false;
        var (fichier, prefixe) = Cible(exe);
        var psi = new ProcessStartInfo(fichier) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe) };
        foreach (var a in prefixe) psi.ArgumentList.Add(a);
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        try { Process.Start(psi); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Le lanceur quand il est à côté de l'App, avec le nom de l'App en premier argument ; sinon l'App elle-même.</summary>
    public static (string Fichier, IReadOnlyList<string> Prefixe) Cible(string cheminApp)
    {
        var lanceur = Path.Combine(Path.GetDirectoryName(cheminApp) ?? "", Lanceur);
        return File.Exists(lanceur) ? (lanceur, [Path.GetFileName(cheminApp)]) : (cheminApp, []);
    }
}
