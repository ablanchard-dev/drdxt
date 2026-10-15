using System.IO;

namespace DrDxt.App.Tests;

// La fenêtre sonde des tests machine est dessinée par PowerShell. Lancé depuis le dossier de Windows, DrDXT sait que ce
// n'est pas un jeu et l'écarte, à raison : la sonde tourne donc sous un nom de jeu inconnu, hors de ce dossier.
internal static class SondeDeJeu
{
    private static readonly Lazy<string> Copie = new(() =>
    {
        var dossier = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "drdxt-sonde-de-jeu")).FullName;
        var exe = Path.Combine(dossier, "SondeJeu.exe");
        if (!File.Exists(exe))
            File.Copy(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), exe);
        return exe;
    });

    public static string Exe => Copie.Value;
}
