using System.Windows.Automation;
using System.Windows.Controls;
using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

// « 12 points à corriger » apparaissait figé. Le chiffre monte jusqu'à sa
// valeur ; la fin est toujours la vraie phrase, et un lecteur d'écran la reçoit tout de suite.
public sealed class CompteurTests
{
    [Theory]
    [InlineData("12 points à corriger", 0.5, "6 points à corriger")]
    [InlineData("12 points à corriger", 0.0, "0 points à corriger")]
    [InlineData("12 points à corriger", 1.0, "12 points à corriger")]
    [InlineData("tout lu, rien à corriger", 0.5, "tout lu, rien à corriger")]
    public void Une_etape_remplace_le_premier_nombre_par_sa_fraction(string texte, double avancement, string attendu)
        => Compteur.Etape(texte, avancement).Should().Be(attendu);

    [Fact]
    public void Sans_animation_permise_la_vraie_phrase_s_affiche_d_un_coup()
    {
        var (texte, nom) = StaApplication.Run(() =>
        {
            var t = new TextBlock();
            Compteur.Poser(t, "12 points à corriger", animer: false);
            return (t.Text, AutomationProperties.GetName(t));
        });

        texte.Should().Be("12 points à corriger");
        nom.Should().Be("12 points à corriger", "le lecteur d'écran reçoit la vraie valeur, jamais un chiffre en route");
    }

    [Fact]
    public void Avec_animation_la_phrase_part_de_zero_et_le_lecteur_a_deja_la_vraie()
    {
        var (texte, nom) = StaApplication.Run(() =>
        {
            var t = new TextBlock();
            Compteur.Poser(t, "12 points à corriger", animer: true);
            return (t.Text, AutomationProperties.GetName(t));
        });

        texte.Should().Be("0 points à corriger");
        nom.Should().Be("12 points à corriger");
    }

    // Un chiffre qui revient chaque scan se lit vite : 300 ms, pas 600.
    [Fact]
    public void La_montee_dure_300_ms() => Compteur.Duree.Should().Be(TimeSpan.FromMilliseconds(300));

    // Le même résultat après un nouveau scan ne repart pas de zéro : rien n'a changé, rien ne bouge.
    [Fact]
    public void Une_valeur_inchangee_ne_rejoue_pas_la_montee()
    {
        var texte = StaApplication.Run(() =>
        {
            var t = new TextBlock();
            Compteur.Poser(t, "12 points à corriger", animer: false);
            Compteur.Poser(t, "12 points à corriger", animer: true);
            return t.Text;
        });

        texte.Should().Be("12 points à corriger");
    }
}
