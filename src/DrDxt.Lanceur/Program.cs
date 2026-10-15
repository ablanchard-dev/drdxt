using System.Diagnostics;
using System.Security.Principal;
using DrDxt.Lanceur;

// -----------------------------------------------------------------------
// DrDxt.Lanceur — l'exécutable natif qui reçoit l'élévation.
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
    private const string Relance = "--relance-elevee";

    private static int Main(string[] args)
    {
        if (args.Length == 0) return 2;
        var nom = args[0];
        var reparer = args.Length == 1 && string.Equals(nom, EnvironnementPropre.ReparerWindows, StringComparison.Ordinal);
        if (!reparer && !EnvironnementPropre.Executables.Contains(nom, StringComparer.OrdinalIgnoreCase)) return 2;
        var dossier = AppContext.BaseDirectory;
        var exe = Path.Combine(dossier, nom);
        if (!reparer && !File.Exists(exe)) return 3;

        if (!EstEleve())
        {
            // UAC désactivé (EnableLUA=0) sur un compte limité, « runas » lance sans élever, et le
            // fils se relançait sans fin. Un seul essai : relancé et toujours pas élevé, on rend « élévation refusée ».
            if (args.Contains(Relance, StringComparer.Ordinal)) return ElevationRefusee;
            var moi = Environment.ProcessPath;
            if (moi is null) return 1;
            var relance = new ProcessStartInfo(moi) { UseShellExecute = true, Verb = "runas", WorkingDirectory = dossier };
            foreach (var a in args) relance.ArgumentList.Add(a);
            relance.ArgumentList.Add(Relance);
            try { Process.Start(relance); return 0; }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ElevationRefusee) { return ElevationRefusee; }
            catch (Exception) { return 1; }
        }

        var (fichier, arguments) = reparer
            ? EnvironnementPropre.ConsoleDeReparation(Environment.SystemDirectory)
            : (exe, "");
        var psi = new ProcessStartInfo(fichier)
        {
            UseShellExecute = false,   // CreateProcess avec NOTRE bloc d'environnement, rien d'hérité.
            // Le helper est un programme console sans rien à montrer : sans fenêtre. Le banc du zip, lui, affiche ses
            // étapes et attend une touche à la fin : sa fenêtre reste.
            CreateNoWindow = !reparer && !string.Equals(nom, "DrDXT-Test.exe", StringComparison.OrdinalIgnoreCase),
            WorkingDirectory = dossier,
        };
        if (reparer) psi.Arguments = arguments;
        else for (var i = 1; i < args.Length; i++) if (args[i] != Relance) psi.ArgumentList.Add(args[i]);
        psi.Environment.Clear();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var localAppData = Dossier(Environment.SpecialFolder.LocalApplicationData);
        var temp = localAppData is null ? Path.Combine(windows, "Temp") : Path.Combine(localAppData, "Temp");
        var systeme = new EnvironnementPropre.DossiersDuSysteme(
            Dossier(Environment.SpecialFolder.ProgramFiles), Dossier(Environment.SpecialFolder.ProgramFilesX86),
            Dossier(Environment.SpecialFolder.CommonProgramFiles), Dossier(Environment.SpecialFolder.CommonProgramFilesX86),
            Dossier(Environment.SpecialFolder.CommonApplicationData), DossierPublic(),
            Environment.UserName, Environment.ProcessorCount, Architecture());
        foreach (var (cle, valeur) in EnvironnementPropre.Construire(windows, temp,
                     Dossier(Environment.SpecialFolder.UserProfile), Dossier(Environment.SpecialFolder.ApplicationData), localAppData, systeme))
            psi.Environment[cle] = valeur;
        try
        {
            using var fils = Process.Start(psi);
            if (fils is null) return 1;
            // La console de réparation : l'App attend sa fermeture pour relire la santé de Windows.
            if (reparer) fils.WaitForExit();
            return 0;
        }
        catch (Exception) { return 1; }
    }

    private static string? Dossier(Environment.SpecialFolder dossier)
    {
        try { var d = Environment.GetFolderPath(dossier); return string.IsNullOrEmpty(d) ? null : d; }
        catch (Exception) { return null; }
    }

    // Pas de dossier connu « Public » dans SpecialFolder : son bureau commun en est un sous-dossier.
    private static string? DossierPublic() =>
        Dossier(Environment.SpecialFolder.CommonDesktopDirectory) is { } bureau ? Path.GetDirectoryName(bureau) : null;

    private static string Architecture() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "AMD64",
    };

    private static bool EstEleve()
    {
        using var identite = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identite).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
