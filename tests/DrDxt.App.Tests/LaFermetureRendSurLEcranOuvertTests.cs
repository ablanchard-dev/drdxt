using System.IO;
using DrDxt.App.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.App.Tests;

/// <summary>
/// ScanViewModel est transitoire : chaque GetService en construit un neuf. OnExit en demandait deux, qui ne
/// savaient rien de l'essai expert ni de la recherche de sous-tension en cours ; la tension d'origine n'était donc
/// pas rendue à la fermeture. La fermeture agit sur l'écran ouvert, gardé par l'App.
/// </summary>
public sealed class LaFermetureRendSurLEcranOuvertTests
{
    [Fact]
    public void Le_conteneur_rend_un_ecran_neuf_a_chaque_demande()
    {
        // Le piège lui-même, prouvé : c'est pourquoi OnExit ne doit pas demander l'écran au conteneur.
        using var provider = new ServiceCollection().AddDrDxtApp().BuildServiceProvider();
        provider.GetRequiredService<ScanViewModel>().Should().NotBeSameAs(provider.GetRequiredService<ScanViewModel>());
    }

    [Fact]
    public void OnExit_rend_sur_l_ecran_ouvert_et_non_sur_un_ecran_neuf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "DrDxt.App", "App.xaml.cs"));
        var debut = source.IndexOf("protected override void OnExit", StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0);
        var corps = source[debut..];

        corps.Should().NotContain("GetService<ScanViewModel>", "un écran neuf ne sait rien de l'essai en cours");
        corps.Should().Contain("_ecranOuvert");
        // OnExit bloque le fil d'interface, où la recherche finit son finally : le vrai rendu se fait à la fermeture de la fenêtre.
        source[..debut].Should().Contain("window.Closing").And.Contain("RendreAvantFermetureAsync");
    }

    // Un second clic sur la croix pendant le rendu fermait l'App au milieu : il attend la fin, et seul le Close lancé par le
    // rendu fini passe.
    [Fact]
    public void Un_second_clic_sur_la_croix_attend_la_fin_du_rendu()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "DrDxt.App", "App.xaml.cs"));
        var debut = source.IndexOf("window.Closing +=", StringComparison.Ordinal);
        var corps = source[debut..source.IndexOf("window.Content =", debut, StringComparison.Ordinal)];

        corps.Should().Contain("if (_fermetureLancee) { fermeture.Cancel = true; return; }")
            .And.Contain("finally { _fermetureAutorisee = true; window.Close(); }");
        corps.IndexOf("_fermetureAutorisee) return;", StringComparison.Ordinal).Should()
            .BeLessThan(corps.IndexOf("if (_fermetureLancee)", StringComparison.Ordinal), "le Close du rendu fini passe avant la garde");
    }

    [Fact]
    public async Task Une_recherche_en_cours_est_quelque_chose_a_rendre()
    {
        using var provider = new ServiceCollection().AddDrDxtApp().BuildServiceProvider();
        var ecran = provider.GetRequiredService<ScanViewModel>();
        ecran.QuelqueChoseARendre.Should().BeFalse();

        ecran.RechercheSousTensionEnCours = true;
        ecran.QuelqueChoseARendre.Should().BeTrue();

        ecran.RechercheSousTensionEnCours = false;
        await ecran.RendreAvantFermetureAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
}
