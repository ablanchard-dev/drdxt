using DrDxt.App.ViewModels;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>« 1 partie(s) relevée(s), 2 arrêt(s) brutal(aux) » : l'interface n'avait
/// pas compté. Chaque mot marqué s'accorde au nombre qui le précède dans la même proposition.</summary>
public sealed class AccordDesPlurielsTests
{
    [Theory]
    [InlineData("Cette semaine : 1 partie(s) relevée(s), 2 changement(s), 3 arrêt(s) brutal(aux), 0 erreur(s) matérielle(s).",
        "Cette semaine : 1 partie relevée, 2 changements, 3 arrêts brutaux, 0 erreur matérielle.")]
    [InlineData("1 arrêt(s) brutal(aux)", "1 arrêt brutal")]
    [InlineData("{1} changement(s) prouvé(s), sur tes parties", "{1} changement(s) prouvé(s), sur tes parties")]
    [InlineData("12 réglage(s) posé(s), relu(s) et remis d'origine ; 1 non touché(s) (déjà réglé(s), illisible(s))",
        "12 réglages posés, relus et remis d'origine ; 1 non touché (déjà réglé, illisible)")]
    [InlineData("Posé(s), effectif(s) tout de suite :", "Posé(s), effectif(s) tout de suite :")]
    public void Chaque_mot_marque_s_accorde_au_nombre_qui_le_precede(string brut, string accorde)
        => Pluriels.Accorder(brut, new System.Globalization.CultureInfo("fr")).Should().Be(accorde);

    // Un nombre trop long ne fait plus planter l'écran ; l'anglais met 0 au pluriel.
    [Fact]
    public void Un_nombre_trop_long_ne_plante_pas_et_l_anglais_a_sa_regle()
    {
        var fr = new System.Globalization.CultureInfo("fr");
        Pluriels.Accorder("Tâche S-1-5-21-3623811015-3361044348 : 2 échec(s)", fr).Should().EndWith("2 échecs");
        Pluriels.Accorder("This week: 0 game(s) recorded, 1 change(s)", new System.Globalization.CultureInfo("en"))
            .Should().Be("This week: 0 games recorded, 1 change");
    }
}
