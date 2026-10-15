using System.Diagnostics;
using System.Text;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

[Collection(CollectionFenetres.Nom)]
public sealed class GameLocatorReduitOnThisMachineTests
{
    // Un plein écran exclusif est réduit par Alt+Tab, et le jeu continue de tourner. La
    // fenêtre réduite reste « visible » mais ne couvre plus l'écran : LocateRunningGamePid ne la voit
    // plus, et les bancs (mémoire, processeur) seraient repartis sous la charge du jeu. On reproduit la
    // fenêtre dans un processus ENFANT (le locator s'ignore lui-même), quasi transparente, puis réduite.
    // Le cas de contrôle (une petite fenêtre réduite) empêche le test de passer à vide.
    [Theory]
    [Trait("Category", "Machine")] // ouvre une fenetre dans la vraie session : rien sur un runner CI
    [InlineData(true)]
    [InlineData(false)]
    public void Un_plein_ecran_reduit_compte_comme_un_jeu_ouvert_mais_pas_comme_le_jeu_a_capturer(bool pleinEcran)
    {
        var locator = new Win32GameLocator();
        var avant = locator.JeuOuvert();
        var pidAvant = locator.LocateRunningGamePid();

        using var enfant = Process.Start(new ProcessStartInfo(SondeDeJeu.Exe,
            "-NoProfile -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(pleinEcran))))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            enfant.StandardOutput.ReadLine().Should().Be("pret");
            var fenetre = new IntPtr(long.Parse(enfant.StandardOutput.ReadLine()!));

            // La fenêtre sonde elle-même, pas « une fenêtre quelconque » : sinon une autre fenêtre de la
            // machine ferait passer le test à vide.
            Win32GameLocator.EstPleinEcranReduit(fenetre, (uint)Environment.ProcessId).Should().Be(pleinEcran);

            // La capture n'est jamais dirigée vers un jeu réduit : il n'affiche rien.
            locator.LocateRunningGamePid().Should().Be(pidAvant);
            // Réduite, la fenêtre sonde n'est plus « affichée » : la surveillance de la capture le verra.
            locator.EstAffiche(enfant.Id).Should().BeFalse();

            if (pleinEcran)
                locator.JeuOuvert().Should().BeTrue("un plein écran réduit est un jeu qui tourne encore");
            else if (!avant)
                locator.JeuOuvert().Should().BeFalse("une petite fenêtre réduite n'est pas un jeu");
            else
                // Un vrai jeu était déjà ouvert : seul ce qui reste démontrable est affirmé.
                locator.JeuOuvert().Should().BeTrue();
        }
        finally
        {
            try { enfant.Kill(); } catch { /* deja termine */ }
            // Kill n'attend pas : sa fenêtre plein écran survivait parfois au test suivant, qui la relevait.
            enfant.WaitForExit(5000);
        }
    }

    // Affichée (non réduite) : vue comme affichée. Le premier plan n'est pas garanti ici ; que la réponse ne
    // dépende pas du focus, c'est le code qui le porte (il n'interroge que la taille de la fenêtre).
    [Fact]
    [Trait("Category", "Machine")] // ouvre une fenetre dans la vraie session : rien sur un runner CI
    public void Une_fenetre_plein_ecran_affichee_est_vue_comme_affichee()
    {
        using var enfant = Process.Start(new ProcessStartInfo(SondeDeJeu.Exe,
            "-NoProfile -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(ScriptAffichee())))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            enfant.StandardOutput.ReadLine().Should().Be("pret");
            enfant.StandardOutput.ReadLine();
            new Win32GameLocator().EstAffiche(enfant.Id).Should().BeTrue();
            new Win32GameLocator().EstAffiche(Environment.ProcessId).Should().BeFalse("temoin : un processus sans fenêtre plein écran");
        }
        finally
        {
            try { enfant.Kill(); } catch { /* deja termine */ }
            // Kill n'attend pas : sa fenêtre plein écran survivait parfois au test suivant, qui la relevait.
            enfant.WaitForExit(5000);
        }
    }

    private static string ScriptAffichee() => """
        Add-Type -AssemblyName System.Windows.Forms
        $f = New-Object System.Windows.Forms.Form
        $f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.Opacity = 0.01; $f.ShowInTaskbar = $false
        $f.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $f.Show(); [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 200
        [Console]::Out.WriteLine('pret'); [Console]::Out.WriteLine($f.Handle.ToInt64()); [Console]::Out.Flush()
        while ($true) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
        """;

    private static string Script(bool pleinEcran) => $$"""
        Add-Type -AssemblyName System.Windows.Forms
        $ecran = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $f = New-Object System.Windows.Forms.Form
        $f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.Opacity = 0.01; $f.ShowInTaskbar = $false
        if (${{(pleinEcran ? "true" : "false")}}) { $f.Bounds = $ecran } else { $f.Bounds = New-Object System.Drawing.Rectangle 0, 0, 300, 200 }
        $f.Show(); [System.Windows.Forms.Application]::DoEvents()
        $f.WindowState = 'Minimized'; [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 200
        [Console]::Out.WriteLine('pret'); [Console]::Out.WriteLine($f.Handle.ToInt64()); [Console]::Out.Flush()
        while ($true) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
        """;
}
