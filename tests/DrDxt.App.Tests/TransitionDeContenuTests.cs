using System.IO;
using System.Windows.Controls;
using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 29/09/2026 : « des animations partout ». Changer de domaine dans le rail remplaçait le panneau d'un coup :
// l'entrée en scène (Revelation) ne joue qu'à l'apparition, pas au changement de contenu. Le panneau entre
// maintenant par un fondu et un glissement court, sous le gouverneur : rien ne bouge quand il l'interdit.
public sealed class TransitionDeContenuTests
{
    [Fact]
    public void Un_changement_de_contenu_anime_le_panneau_quand_le_gouverneur_l_autorise()
    {
        var (autorise, interdit) = StaApplication.Run(() =>
        {
            var anime = new ContentControl();
            TransitionDeContenu.Jouer(anime, autorise: true);
            var fige = new ContentControl();
            TransitionDeContenu.Jouer(fige, autorise: false);
            return (anime.HasAnimatedProperties, fige.HasAnimatedProperties);
        });

        autorise.Should().BeTrue("le panneau entre en scène");
        interdit.Should().BeFalse("témoin : jeu au premier plan ou animations coupées, rien ne bouge");
    }

    [Fact]
    public void La_transition_est_branchee_sur_le_panneau_des_domaines()
    {
        var xaml = File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.App", "Views", "ScanView.xaml"));
        xaml.Should().Contain("<ContentControl Content=\"{Binding DomaineSelectionne}\" Focusable=\"False\" effets:TransitionDeContenu.Actif=\"True\"");
    }

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDxt.sln")) && !Directory.Exists(Path.Combine(d.FullName, "src")))
            d = d.Parent;
        return d!.FullName;
    }
}
