using System.IO;
using FluentAssertions;
using DrDxt.App;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. « Site responsive » de la liste produit, applique a l'application.
//
// `MainWindow.xaml` demande une taille de depart de **1320 x 900 DIP**, sans
// jamais regarder la place disponible. WPF ne redimensionne pas : il ouvre a la
// taille demandee, quitte a deborder. Deux cas ordinaires, tous deux mesurables
// a la main :
//
//   * un portable 1366 x 768 : la zone de travail fait ~728 DIP de haut une fois
//     la barre des taches retiree. 900 demandes ⇒ **172 DIP sous l'ecran**.
//   * un 1920 x 1080 a 150 % (le reglage que Windows recommande sur beaucoup de
//     portables) : la zone de travail vaut ~1280 x 673 DIP. La fenetre deborde
//     alors des deux cotes.
//
// Ce qui depasse, c'est le bas de la fenetre -- la ou se trouve l'action. Et le
// premier ecran d'un produit payant est celui qu'on ne recommence pas.
//
// La reponse n'est pas de rapetisser la fenetre pour tout le monde : sur un
// 1440p elle doit rester grande. Elle est de **s'adapter a la place qu'on a**,
// sans jamais descendre sous le minimum utilisable -- si l'ecran est plus petit
// que ce minimum, c'est le minimum qui gagne et l'utilisateur deplacera la
// fenetre ; on ne rend pas l'interface inutilisable pour eviter un debordement.
//
// La regle est une fonction pure, testee ici sans ouvrir la moindre fenetre :
// aucune de ces assertions n'a besoin d'un ecran, donc elles tournent en CI.
// -----------------------------------------------------------------------
public sealed class LaFenetreTientDansLEcranQuOnATests
{
    [Fact]
    public void Sur_un_grand_ecran_la_taille_voulue_est_gardee()
    {
        // Temoin : sans lui, une regle qui rapetisse tout serait verte partout.
        var (largeur, hauteur) = TailleDeDepart.Adaptee(
            voulueLargeur: 1320, voulueHauteur: 900,
            zoneLargeur: 2560, zoneHauteur: 1392);

        largeur.Should().Be(1320);
        hauteur.Should().Be(900);
    }

    [Theory]
    // 1366x768, barre des taches retiree.
    [InlineData(1366, 728)]
    // 1920x1080 a 150 % : la zone de travail vaut ~1280 x 673 DIP.
    [InlineData(1280, 673)]
    // 1600x900 a 125 % : ~1280 x 672 DIP.
    [InlineData(1280, 672)]
    public void Sur_un_ecran_etroit_la_fenetre_ne_deborde_pas(double zoneLargeur, double zoneHauteur)
    {
        var (largeur, hauteur) = TailleDeDepart.Adaptee(1320, 900, zoneLargeur, zoneHauteur);

        largeur.Should().BeLessThanOrEqualTo(zoneLargeur,
            "une fenetre plus large que la zone de travail s'ouvre en partie hors de l'ecran");
        hauteur.Should().BeLessThanOrEqualTo(zoneHauteur,
            "ce qui depasse en bas, c'est l'action de l'ecran");
    }

    [Theory]
    // Les configurations ou la fenetre doit pouvoir etre rendue entierement visible.
    [InlineData(1366, 728)]   // 1366x768, barre des taches retiree
    [InlineData(1280, 673)]   // 1920x1080 a 150 %
    [InlineData(1280, 672)]   // 1600x900 a 125 %
    public void Le_minimum_lui_meme_tient_dans_les_ecrans_ordinaires(double zoneLargeur, double zoneHauteur)
    {
        // Ce test a ete ecrit apres coup, parce que la mesure a contredit le precedent.
        // Le minimum valait `MinHeight="680"` : sur un 1080p a 150 % (673 DIP de zone de
        // travail) et sur un 1600x900 a 125 % (672), la fenetre ne pouvait jamais tenir --
        // meme redimensionnee a la main par l'utilisateur. Le defaut n'etait pas la taille
        // de depart, il etait dans la borne. 640 tient les trois avec de la marge.
        TailleDeDepart.MinimumHauteur.Should().BeLessThanOrEqualTo(zoneHauteur);
        TailleDeDepart.MinimumLargeur.Should().BeLessThanOrEqualTo(zoneLargeur);
    }

    [Fact]
    public void Le_minimum_utilisable_l_emporte_sur_le_debordement()
    {
        // L'autre sens, et il compte : sur un ecran plus petit que le minimum, rapetisser
        // encore ne repare rien -- ca rend l'interface inutilisable et elle deborde quand meme.
        var (largeur, hauteur) = TailleDeDepart.Adaptee(1320, 900, zoneLargeur: 800, zoneHauteur: 500);

        largeur.Should().Be(TailleDeDepart.MinimumLargeur);
        hauteur.Should().Be(TailleDeDepart.MinimumHauteur);
    }

    [Fact]
    public void Une_zone_de_travail_absurde_ne_fait_pas_disparaitre_la_fenetre()
    {
        // Un ecran peut etre rapporte a 0 pendant un changement de configuration (sortie de
        // veille, branchement d'un second moniteur). Une taille 0 serait une fenetre invisible.
        var (largeur, hauteur) = TailleDeDepart.Adaptee(1320, 900, zoneLargeur: 0, zoneHauteur: 0);

        largeur.Should().Be(TailleDeDepart.MinimumLargeur);
        hauteur.Should().Be(TailleDeDepart.MinimumHauteur);
    }

    [Fact]
    public void Les_bornes_du_code_sont_celles_du_XAML()
    {
        // Deux surfaces portent le meme minimum : `MinWidth`/`MinHeight` dans le XAML et les
        // constantes ici. Rien ne les tenait ensemble -- c'est le motif de la journee.
        var xaml = File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.App", "MainWindow.xaml"));

        xaml.Should().Contain($"MinHeight=\"{TailleDeDepart.MinimumHauteur}\"");
        xaml.Should().Contain($"MinWidth=\"{TailleDeDepart.MinimumLargeur}\"");
    }

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
