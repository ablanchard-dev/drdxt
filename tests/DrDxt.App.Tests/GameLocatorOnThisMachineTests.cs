using System.Runtime.InteropServices;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

[Collection(CollectionFenetres.Nom)]
public sealed class GameLocatorOnThisMachineTests
{
    // 14/09/2026 — mesure sur le poste de dev, aucun jeu lance : la regle « fenetre visible de la
    // taille de l'ecran » retenait TextInputHost, une CoreWindow masquee par DWM. Le produit aurait
    // mesure l'hote du clavier tactile a la place du jeu. Le test appelle le vrai locator.
    [Fact]
    [Trait("Category", "Machine")] // lit les fenetres de la vraie session : vide sur un runner CI
    public void A_cloaked_system_window_is_never_taken_for_the_game()
    {
        var pid = new Win32GameLocator().LocateRunningGamePid();

        var nom = pid is { } p ? ((IGameLocator)new NullGameLocator()).NameOfGame(p) : null;
        nom.Should().NotBe("TextInputHost");
    }

    // 26/09/2026 : mesuré sur le poste en usage normal : le localisateur désignait explorer, par intermittence
    // (3 fois sur 3 en sondant 10 s, fenêtre XamlExplorerHostIslandWindow de la taille de l'écran principal).
    // Limite dite : ce test ne rougit que si une fenêtre passagère du shell est affichée pendant qu'il sonde.
    [Fact]
    [Trait("Category", "Machine")] // lit les fenetres de la vraie session : vide sur un runner CI
    public void Aucune_fenetre_du_shell_n_est_prise_pour_le_jeu()
    {
        GetWindowThreadProcessId(GetShellWindow(), out var pidShell);
        pidShell.Should().NotBe(0u, "temoin : le shell de la session est trouvé");
        var locator = new Win32GameLocator();

        for (var i = 0; i < 20; i++)
        {
            locator.LocateRunningGamePid().Should().NotBe((int)pidShell);
            Thread.Sleep(100);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
