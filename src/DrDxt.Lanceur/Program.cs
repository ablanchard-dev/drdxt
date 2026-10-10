using System.Diagnostics;
using System.Security.Principal;
using DrDxt.Lanceur;

// -----------------------------------------------------------------------
// DrDxt.Lanceur — l'exécutable natif qui reçoit l'élévation (10/10/2026, revue de sécurité).
//
//   DrDxt.Lanceur.exe <DrDxt.ElevatedHelper.exe | DrDxt.App.exe | DrDXT-Test.exe> [arguments du programme]
//
// 1. Pas élevé : se relance lui-même avec le verbe « runas » (l'invite Windows porte ce programme) et s'arrête.
// 2. Élevé : démarre le programme demandé, À CÔTÉ de lui (jamais un chemin reçu), avec un environnement
//    reconstruit (EnvironnementPropre) et sans fenêtre, puis s'arrête sans attendre. Le fils hérite du jeton
//    élevé : aucune seconde invite.
// Codes de sortie : 0 lancé, 2 usage, 3 programme absent, 1223 élévation refusée, 1 autre échec.
// -----------------------------------------------------------------------
internal static class Program
{
    private const int ElevationRefusee = 1223;

    private static int Main(string[] args)
    {
        if (args.Length == 0) return 2;
        var nom = args[0];
        if (!EnvironnementPropre.Executables.Contains(nom, StringComparer.OrdinalIgnoreCase)) return 2;
        var dossier = AppContext.BaseDirectory;
        var exe = Path.Combine(dossier, nom);
        if (!File.Exists(exe)) return 3;

        if (!EstEleve())
        {
            var moi = Environment.ProcessPath;
            if (moi is null) return 1;
            var relance = new ProcessStartInfo(moi) { UseShellExecute = true, Verb = "runas", WorkingDirectory = dossier };
            foreach (var a in args) relance.ArgumentList.Add(a);
            try { Process.Start(relance); return 0; }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ElevationRefusee) { return ElevationRefusee; }
            catch (Exception) { return 1; }
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,   // CreateProcess avec NOTRE bloc d'environnement, rien d'hérité.
            CreateNoWindow = true,
            WorkingDirectory = dossier,
        };
        for (var i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
        psi.Environment.Clear();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var localAppData = Dossier(Environment.SpecialFolder.LocalApplicationData);
        var temp = localAppData is null ? Path.Combine(windows, "Temp") : Path.Combine(localAppData, "Temp");
        foreach (var (cle, valeur) in EnvironnementPropre.Construire(windows, temp,
                     Dossier(Environment.SpecialFolder.UserProfile), Dossier(Environment.SpecialFolder.ApplicationData), localAppData))
            psi.Environment[cle] = valeur;
        try { return Process.Start(psi) is null ? 1 : 0; }
        catch (Exception) { return 1; }
    }

    private static string? Dossier(Environment.SpecialFolder dossier)
    {
        try { var d = Environment.GetFolderPath(dossier); return string.IsNullOrEmpty(d) ? null : d; }
        catch (Exception) { return null; }
    }

    private static bool EstEleve()
    {
        using var identite = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identite).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
