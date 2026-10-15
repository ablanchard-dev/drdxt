using System.IO;
using System.Text.RegularExpressions;
using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Les durées des animations qu'on voit souvent. Une sortie est plus rapide qu'une entrée ; ce qui revient des dizaines de
/// fois par jour (le survol d'Optimiser) ne rejoue pas son effet à chaque fois ; une coche apparaît vite et s'efface sans traîner.
/// </summary>
public sealed class LesAnimationsSuiventLeursDureesTests
{
    private static string Fichier(params string[] chemin)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent!;
        return File.ReadAllText(Path.Combine(new[] { d.FullName, "src", "DrDxt.App" }.Concat(chemin).ToArray()));
    }

    [Fact]
    public void La_coche_fait_apparait_en_120_ms_et_s_efface_en_200()
    {
        NoyauDeMaintien.CocheEntree.Should().Be(TimeSpan.FromMilliseconds(120));
        NoyauDeMaintien.CocheSortie.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void Le_survol_de_la_bille_entre_en_200_ms_et_sort_en_150()
    {
        var vue = Fichier("Views", "ScanView.xaml");
        vue.Should().Contain(@"TargetName=""Bille"" Storyboard.TargetProperty=""Luminosite"" To=""1.18"" Duration=""0:0:0.2""");
        vue.Should().Contain(@"TargetName=""Bille"" Storyboard.TargetProperty=""Luminosite"" To=""1"" Duration=""0:0:0.15""");
    }

    // Le reflet joue sans geste, à l'apparition du résultat : il passe par le moteur d'animation, qui le refuse quand Windows
    // coupe les effets ou que la fenêtre n'est pas au premier plan. Un retour à un geste (survol, appui) reste permis.
    [Fact]
    public void Le_reflet_d_optimiser_passe_a_l_apparition_par_le_moteur_et_plus_au_survol()
    {
        var app = Fichier("App.xaml");
        var debut = app.IndexOf(@"x:Key=""CapsuleChrome""", StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0, "témoin : le style de la capsule chrome est lu");
        var style = app[debut..app.IndexOf("</Style>", debut, StringComparison.Ordinal)];

        Regex.Match(style, @"<Trigger Property=""IsMouseOver"" Value=""True"">.*?</Trigger>", RegexOptions.Singleline).Value
            .Should().NotBeEmpty().And.NotContain("Eclat", "un survol revient des dizaines de fois par jour");
        style.Should().NotContain(@"Property=""IsVisible""", "un déclencheur XAML ne consulte pas le moteur d'animation");
        style.Should().Contain("effets:RefletALApparition.Actif", "le reflet est lancé par le code, qui consulte le moteur");
    }

    [Fact]
    public void Sans_moteur_actif_le_reflet_ne_demarre_pas()
    {
        var (fige, anime) = StaApplication.Run(() =>
        {
            var b = new System.Windows.Controls.Button { Style = (System.Windows.Style)System.Windows.Application.Current.Resources["CapsuleChrome"] };
            b.ApplyTemplate();
            return (RefletALApparition.Jouer(b, animer: false), RefletALApparition.Jouer(b, animer: true));
        });

        fige.Should().BeFalse("animations coupées ou fenêtre derrière le jeu : rien ne joue");
        anime.Should().BeTrue("témoin : avec le moteur actif, le reflet part bien");
    }

    // À sa première apparition le bouton n'a pas encore de gabarit : le reflet le construit, et ne rejoue pas ensuite.
    [Fact]
    public void Le_reflet_part_sans_gabarit_construit_et_une_seule_fois()
    {
        var (premier, second) = StaApplication.Run(() =>
        {
            var b = new System.Windows.Controls.Button { Style = (System.Windows.Style)System.Windows.Application.Current.Resources["CapsuleChrome"] };
            return (RefletALApparition.AuPremierAffichage(b, animer: true), RefletALApparition.AuPremierAffichage(b, animer: true));
        });

        premier.Should().BeTrue("le gabarit se construit au premier affichage");
        second.Should().BeFalse("revenir sur la page ne rejoue pas le reflet");
    }
}
