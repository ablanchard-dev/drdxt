using System.IO;

namespace DrDxt.App.Platform;

/// <summary>
/// 06/10/2026 (chantier J1b) : le relevé des parties sans l'App ouverte. Le 06/10, une partie jouée App fermée
/// n'a laissé aucune trace. Relevé allumé, l'App pose un raccourci dans le dossier Démarrage du joueur (aucun droit
/// administrateur, aucune écriture dans le registre) et s'y lance sans fenêtre, juste pour le
/// relevé ; l'arrêter retire le raccourci. Démarrage de session plutôt que service : un service
/// tourne en session 0, hors du compte du joueur.
/// </summary>
internal static class ReleveAuDemarrage
{
    public const string Argument = "--releve";
    // Sans accent : l'installeur retire le même fichier à la désinstallation ([UninstallDelete], {userstartup}).
    internal const string NomRaccourci = "DrDXT releve.lnk";

    private static string Raccourci => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), NomRaccourci);

    public static void Regler(bool allume) => Regler(allume, Environment.ProcessPath,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), exe => Poser(exe, Raccourci), () => File.Delete(Raccourci));

    /// <summary>Allumé : pose, seulement depuis Program Files (un build de développement ne s'inscrit pas au démarrage
    /// du joueur). Éteint : retire, d'où qu'on vienne.</summary>
    internal static void Regler(bool allume, string? exe, string programFiles, Action<string> poser, Action retirer)
    {
        try
        {
            if (!allume) retirer();
            else if (exe is not null && SousProgramFiles(exe, programFiles)) poser(exe);
        }
        catch (Exception) { /* un raccourci refusé (hôte de scripts absent, dossier interdit) n'arrête ni l'App ni le relevé ouvert */ }
    }

    private static bool SousProgramFiles(string exe, string programFiles)
    {
        if (string.IsNullOrWhiteSpace(programFiles)) return false;
        var dossier = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(exe).StartsWith(dossier, StringComparison.OrdinalIgnoreCase);
    }

    // Le raccourci par l'objet COM de l'hôte de scripts Windows, plutôt qu'IShellLink déclaré à la main.
    internal static void Poser(string exe, string raccourci)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        var lien = shell.CreateShortcut(raccourci);
        lien.TargetPath = exe;
        lien.Arguments = Argument;
        lien.WorkingDirectory = Path.GetDirectoryName(exe);
        lien.Description = "DrDXT : relevé des parties";
        lien.Save();
    }
}
