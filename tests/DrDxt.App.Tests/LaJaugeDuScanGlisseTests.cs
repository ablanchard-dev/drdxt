using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, D3.1) : « un arc de cercle bleu censé bouger en même temps que le scan, pas très fluide ».
// La jauge sautait d'un huitième à chaque étape puis restait figée. Elle glisse, avance pendant l'étape, sans jamais
// dépasser la fin de l'étape en cours.
public sealed class LaJaugeDuScanGlisseTests
{
    private const double Dt = 1.0 / 30;

    private static double Avancer(double affiche, double valeur, double plafond, double secondes)
    {
        for (var t = 0.0; t < secondes; t += Dt) affiche = ArcDeProgression.Suivant(affiche, valeur, plafond, Dt);
        return affiche;
    }

    [Fact]
    public void Elle_glisse_vers_la_nouvelle_etape_au_lieu_de_sauter()
    {
        var un = ArcDeProgression.Suivant(0, 0.25, 0.25, Dt);
        un.Should().BeGreaterThan(0).And.BeLessThan(0.25, "un seul pas ne saute pas jusqu'au bout");
        Avancer(0, 0.25, 0.25, 1.5).Should().BeApproximately(0.25, 0.001, "rattrapée en moins de deux secondes");
    }

    [Fact]
    public void Pendant_une_etape_elle_avance_sans_jamais_depasser_la_fin_de_l_etape()
    {
        var valeur = 1 / 8.0; var plafond = 1.85 / 8;
        var apres5 = Avancer(valeur, valeur, plafond, 5);
        apres5.Should().BeGreaterThan(valeur, "elle bouge pendant l'étape");
        // Chaque image compte : un dépassement suivi d'un retour serait un saut visible.
        var affiche = valeur;
        for (var t = 0.0; t < 600; t += Dt)
        {
            var suivant = ArcDeProgression.Suivant(affiche, valeur, plafond, Dt);
            suivant.Should().BeLessThanOrEqualTo(plafond, "jamais au-delà de ce que l'analyse a fait");
            suivant.Should().BeGreaterThanOrEqualTo(affiche, "jamais de recul pendant une étape");
            affiche = suivant;
        }
    }

    [Fact]
    public void Elle_ne_recule_jamais_pendant_une_analyse_et_repart_de_zero_pour_la_suivante()
    {
        var affiche = 0.0; var precedente = 0.0;
        for (var etape = 1; etape <= 8; etape++)
        {
            affiche = Avancer(affiche, (etape - 1) / 8.0, (etape - 0.15) / 8, 3);
            affiche.Should().BeGreaterThanOrEqualTo(precedente);
            precedente = affiche;
        }
        affiche = Avancer(affiche, 1, 1, 2);
        affiche.Should().BeApproximately(1, 0.001);

        ArcDeProgression.Suivant(affiche, 0, 0.85 / 8, Dt).Should().Be(0, "nouvelle analyse : retour à zéro immédiat");
    }
}
