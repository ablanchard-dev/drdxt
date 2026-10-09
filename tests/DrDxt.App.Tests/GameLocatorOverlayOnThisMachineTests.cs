using System.Diagnostics;
using System.Text;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

[Collection(CollectionFenetres.Nom)]
public sealed class GameLocatorOverlayOnThisMachineTests
{
    // 14/09/2026 (plan M5) — NVIDIA Overlay pose une fenetre visible de la taille de l'ecran, en
    // WS_EX_NOACTIVATE. On la reproduit dans un processus ENFANT (le locator s'ignore lui-meme),
    // quasi transparente et sans voler le focus, et on interroge le vrai locator.
    // Le cas sans NOACTIVATE est le controle : s'il n'etait pas trouve, le test passerait a vide.
    [Theory]
    [Trait("Category", "Machine")] // ouvre une fenetre dans la vraie session : rien sur un runner CI
    [InlineData(true)]
    [InlineData(false)]
    public void A_window_that_can_never_be_activated_is_not_the_game(bool noActivate)
    {
        using var enfant = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(noActivate))))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        // Corrige le 18/09/2026. Le cas de CONTROLE exigeait que le PID localise soit
        // Exactement celui de la fenetre-sonde. Il est devenu rouge des qu'on a lance une
        // partie : le locator a repondu `JeuBClient-Win64-Shipping`, c'est-a-dire le vrai
        // jeu, premier dans l'enumeration. **Le produit avait raison, le test avait tort** --
        // marque `Category=Machine`, il supposait en plus qu'aucun jeu ne tourne, et c'est
        // precisement la machine ou l'on joue qui l'executait.
        //
        // On mesure donc l'etat avant la sonde, et on affirme ce qui est vrai dans chaque cas
        // plutot que de sauter le test : un test qui se saute ne rougit jamais.
        var avant = new Win32GameLocator().LocateRunningGamePid();
        try
        {
            // L'enfant ecrit « pret » une fois sa fenetre affichee.
            enfant.StandardOutput.ReadLine().Should().Be("pret");

            var apres = new Win32GameLocator().LocateRunningGamePid();

            if (noActivate)
            {
                // La garantie du jour, vraie quoi qu'il y ait d'autre a l'ecran : une fenetre
                // qui ne peut jamais passer au premier plan n'est jamais designee comme le jeu.
                apres.Should().NotBe(enfant.Id,
                    "une fenetre WS_EX_NOACTIVATE est un overlay, pas le jeu auquel on joue");
                apres.Should().Be(avant, "la sonde ne doit rien changer a ce que le produit voit");
            }
            else if (avant is null)
            {
                // Machine au repos : le controle peut etre strict, et il doit l'etre -- sans lui,
                // le cas NOACTIVATE passerait a vide sur une sonde que le locator ne voit pas.
                apres.Should().Be(enfant.Id,
                    "sans NOACTIVATE, la sonde couvre l'ecran : c'est elle que le locator doit trouver");
            }
            else
            {
                // Un vrai jeu tournait deja. Le controle strict est impossible -- et ce n'est pas
                // un defaut : le locator prefere legitimement le jeu, premier dans l'enumeration.
                // Ce qui reste demontrable, et qui suffit ici : la sonde ne le fait pas devier.
                apres.Should().Be(avant,
                    "un vrai jeu tournait deja : poser une fenetre de plus ne doit pas changer la reponse");
            }
        }
        finally
        {
            try { enfant.Kill(); } catch { /* deja termine */ }
            // Kill n'attend pas : sa fenêtre plein écran survivait parfois au test suivant, qui la relevait.
            enfant.WaitForExit(5000);
        }
    }

    private static string Script(bool noActivate) => $$"""
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
        public class FenetreSonde : System.Windows.Forms.Form {
            public static int ExStyle;
            protected override System.Windows.Forms.CreateParams CreateParams {
                get { var cp = base.CreateParams; cp.ExStyle |= ExStyle; return cp; } }
            protected override bool ShowWithoutActivation { get { return true; } }
        }
        '@
        [FenetreSonde]::ExStyle = {{(noActivate ? 0x08000000 : 0)}}
        $f = New-Object FenetreSonde
        $f.FormBorderStyle = 'None'
        $f.StartPosition = 'Manual'
        $f.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $f.ShowInTaskbar = $false
        $f.Opacity = 0.01
        $f.Show()
        [System.Windows.Forms.Application]::DoEvents()
        [Console]::Out.WriteLine('pret'); [Console]::Out.Flush()
        Start-Sleep -Seconds 20
        """;
}
