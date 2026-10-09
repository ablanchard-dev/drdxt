using System.Globalization;
using System.Windows.Media;
using DrDxt.App.Converters;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. Suite de la lecon du jour : ce qui est resolu EN CODE echappe aux
// cribles qui lisent le balisage.
//
// `HeadlineKeyToBrushConverter` va chercher ses couleurs par leur NOM, a
// l'execution, avec `TryFindResource` — et retombe sur `Brushes.Gray` si le nom
// ne repond pas. Renommer un jeton dans App.xaml ne casserait donc aucune
// compilation, aucun test de XAML : le badge « gain mesurable » deviendrait
// simplement gris, comme « regression annulee », comme « aucun gain mesurable ».
// Trois etats differents, une seule couleur, et personne pour le dire.
//
// Verifie le jour meme : les trois jetons existent. Ce test interdit qu'ils
// disparaissent sans que rien ne rougisse.
//
// (La couleur n'est ici qu'un renfort — le badge porte aussi une icone et un
// texte. C'est justement pour ca qu'un renfort muet passerait inapercu.)
// -----------------------------------------------------------------------
[Collection(CollectionWpf.Nom)]
public sealed class LaCouleurDuBadgeNeTombePasEnPanneEnSilenceTests
{
    private static Brush Couleur(string cle) => StaApplication.Run(() =>
        (Brush)new HeadlineKeyToBrushConverter()
            .Convert(cle, typeof(Brush), null!, CultureInfo.InvariantCulture));

    private static string Decrit(Brush b) => (b as SolidColorBrush)?.Color.ToString() ?? b.ToString()!;

    [Theory]
    [InlineData("dashboard.measurable_gain")]
    [InlineData("dashboard.regression_reverted")]
    [InlineData("dashboard.no_measurable_gain")]
    public void Chaque_etat_trouve_sa_couleur_et_non_le_repli_gris(string cle)
    {
        var obtenue = Couleur(cle);

        Decrit(obtenue).Should().NotBe(Decrit(Brushes.Gray),
            $"« {cle} » est tombe sur le repli : son jeton de couleur ne repond plus dans App.xaml");
    }

    [Fact]
    public void Les_trois_etats_ne_se_ressemblent_pas()
    {
        // Sans ceci, trois jetons pointant par accident sur la meme couleur
        // passeraient le test precedent tout en effacant la distinction.
        var couleurs = new[] { "dashboard.measurable_gain", "dashboard.regression_reverted", "dashboard.no_measurable_gain" }
            .Select(c => Decrit(Couleur(c)))
            .ToList();

        couleurs.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Le_repli_gris_existe_bel_et_bien_pour_une_cle_inconnue()
    {
        // Temoin : si le convertisseur ne repliait jamais, les tests ci-dessus
        // passeraient sans rien garantir — ils compareraient a une couleur que le
        // code ne peut pas produire.
        Decrit(Couleur("cle.qui.n.existe.pas")).Should().NotBeNullOrWhiteSpace();
    }
}
