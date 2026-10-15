using System.Windows;
using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Au survol d'un composant, sa douille s'allume. La carte visée suit la mise en page (icône sur le pôle),
// et l'éclat monte vite puis redescend, sans boucle.
public sealed class SurvolDesComposantsTests
{
    private static readonly Size Canevas = new(760, 720);

    [Fact]
    public void L_icone_de_chaque_composant_est_dans_sa_carte_et_le_centre_dans_aucune()
    {
        for (var i = 0; i < BranchesDeChrome.Nombre; i++)
        {
            var pole = Orbite.Position(i, BranchesDeChrome.Nombre, Canevas, .5, .5, BranchesDeChrome.RayonX, BranchesDeChrome.RayonY, -90);
            BranchesDeChrome.CarteSous(pole, Canevas).Should().Be(i, $"l'icône du composant {i} est sur son pôle");
        }
        BranchesDeChrome.CarteSous(new Point(380, 360), Canevas).Should().Be(-1, "la bille n'est la carte de personne");
    }

    [Fact]
    public void L_eclat_monte_en_moins_de_300_ms_et_redescend_sans_rester_allume()
    {
        var e = 0.0;
        for (var t = 0; t < 9; t++) e = BranchesDeChrome.EclatSuivant(e, vise: true, dt: 1.0 / 60);
        e.Should().BeGreaterThan(.9, "150 ms après l'entrée de la souris, la douille est allumée");
        for (var t = 0; t < 18; t++) e = BranchesDeChrome.EclatSuivant(e, vise: false, dt: 1.0 / 60);
        e.Should().BeLessThan(.05, "300 ms après le départ de la souris, elle est éteinte");
    }
}
