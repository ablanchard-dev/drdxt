using System.Windows;
using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Un maintien arrivé au bout ne se voyait pas partir ; l'action
// répondait parfois plusieurs secondes après. L'éclair irisé part à l'instant du déclenchement, sous le gouverneur.
public sealed class MaintienQuiRepondTests
{
    [Theory]
    [InlineData("CapsuleDeMaintien")]
    [InlineData("CapsuleDeMesure")]
    [InlineData(null)]
    public void Le_declenchement_allume_l_eclair_quand_le_gouverneur_l_autorise(string? style)
    {
        var (anime, fige) = StaApplication.Run(() =>
        {
            NoyauDeMaintien Prepare()
            {
                var n = new NoyauDeMaintien();
                // Sans nom, c'est le style implicite du noyau : hors d'un arbre visuel, il faut le poser soi-même.
                n.Style = (Style)System.Windows.Application.Current.Resources[(object?)style ?? typeof(NoyauDeMaintien)];
                n.ApplyTemplate();
                return n;
            }
            var a = Prepare();
            a.Eclairer(animer: true);
            var f = Prepare();
            f.Eclairer(animer: false);
            return (((UIElement)a.Template.FindName("Eclair", a)).HasAnimatedProperties,
                    ((UIElement)f.Template.FindName("Eclair", f)).HasAnimatedProperties);
        });

        anime.Should().BeTrue("l'action part : l'éclair le dit");
        fige.Should().BeFalse("témoin : jeu au premier plan ou animations coupées, rien ne bouge");
    }

    // Annexe de B5 : l'action finie, la capsule le dit par une coche qui apparaît puis s'efface.
    [Fact]
    public void Une_action_finie_montre_la_coche_sur_la_capsule()
    {
        var (anime, fige) = StaApplication.Run(() =>
        {
            NoyauDeMaintien Capsule()
            {
                var n = new NoyauDeMaintien { Style = (Style)System.Windows.Application.Current.Resources["CapsuleDeMaintien"] };
                n.ApplyTemplate();
                return n;
            }
            var a = Capsule();
            a.SignalerFait(animer: true);
            var f = Capsule();
            f.SignalerFait(animer: false);
            return (((UIElement)a.Template.FindName("Fait", a)).HasAnimatedProperties,
                    ((UIElement)f.Template.FindName("Fait", f)).HasAnimatedProperties);
        });

        anime.Should().BeTrue();
        fige.Should().BeFalse("témoin : figé, pas de coche animée");
    }
}
