using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Animation;
using DrDxt.App.Effets;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

/// <summary>
/// Les animations déclarées en XAML ne consultent rien d'elles-mêmes : un déclencheur joue son storyboard même quand Windows a
/// coupé les animations ou que la fenêtre est derrière le jeu. Elles passent donc par deux types qui lisent la règle du moteur
/// à chaque image : une transition de geste se fige quand Windows coupe les effets, un éclat sans geste quand le moteur dort.
/// </summary>
public sealed class LesAnimationsXamlPassentParLeMoteurTests
{
    private static string Fichier(params string[] chemin)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent!;
        var fichier = Path.Combine(new[] { d.FullName, "src", "DrDxt.App" }.Concat(chemin).ToArray());
        return Path.GetFileName(fichier) == "ScanView.xaml" ? VueScan.Texte(fichier) : File.ReadAllText(fichier);
    }

    [Theory]
    [InlineData("App.xaml")]
    [InlineData("Views", "ScanView.xaml")]
    public void Aucune_animation_nue_ne_reste_dans_les_vues(params string[] chemin)
    {
        var xaml = Fichier(chemin);
        Regex.Matches(xaml, @"<(DoubleAnimation|DoubleAnimationUsingKeyFrames|ColorAnimation|ThicknessAnimation)\b")
            .Select(m => m.Value).Should().BeEmpty("une animation XAML nue joue sans consulter le moteur");
        xaml.Should().Contain("effets:", "témoin : les animations gouvernées sont bien là");
    }

    private static double Image(AnimationTimeline animation, double origine, TimeSpan instant)
    {
        var horloge = animation.CreateClock();
        horloge.Controller!.Begin();
        horloge.Controller.SeekAlignedToLastTick(instant, TimeSeekOrigin.BeginTime);
        return (double)animation.GetCurrentValue(origine, origine, horloge);
    }

    [Fact]
    public void Une_transition_de_geste_va_droit_a_sa_valeur_quand_Windows_coupe_les_animations()
    {
        var avant = TransitionDeGeste.Figee;
        try
        {
            var (figee, animee) = StaApplication.Run(() =>
            {
                var t = new TransitionDeGeste { To = 1.18, Duration = TimeSpan.FromMilliseconds(200) };
                TransitionDeGeste.Figee = () => true;
                var f = Image(t, 1, TimeSpan.FromMilliseconds(100));
                TransitionDeGeste.Figee = () => false;
                return (f, Image(t, 1, TimeSpan.FromMilliseconds(100)));
            });

            figee.Should().Be(1.18, "animations coupées : pas d'interpolation, la valeur d'arrivée tout de suite");
            animee.Should().BeGreaterThan(1).And.BeLessThan(1.18, "témoin : animations permises, la transition interpole");
        }
        finally { TransitionDeGeste.Figee = avant; }
    }

    [Fact]
    public void Un_eclat_sans_geste_rend_sa_derniere_valeur_quand_le_moteur_dort()
    {
        var avant = EclatSansGeste.Arrete;
        try
        {
            var (arrete, joue) = StaApplication.Run(() =>
            {
                var e = new EclatSansGeste();
                e.KeyFrames.Add(new EasingDoubleKeyFrame(2.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
                e.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
                EclatSansGeste.Arrete = () => true;
                var a = Image(e, 1, TimeSpan.FromMilliseconds(220));
                EclatSansGeste.Arrete = () => false;
                return (a, Image(e, 1, TimeSpan.FromMilliseconds(220)));
            });

            arrete.Should().Be(1, "fenêtre derrière le jeu ou effets coupés : pas d'éclat, la bille reste posée");
            joue.Should().BeApproximately(2.1, 0.001, "témoin : moteur actif, l'éclat est à son pic");
        }
        finally { EclatSansGeste.Arrete = avant; }
    }
}
