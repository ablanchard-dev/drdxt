using DrDxt.Core;
using DrDxt.Measurement;
using FluentAssertions;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// Le modele de preuve a un producteur reel — sinon il ne vaut rien.
///
/// Un type ecrit et jamais appele est exactement le defaut repare ailleurs ce jour-la :
/// `InstallPresentMonServiceOp` etait allowlistee, testee, et emise par personne pendant des mois.
/// `PerformanceEvidence` ne doit pas prendre le meme chemin.
///
/// `AbComparer.Compare` rend un booleen et **jette la resolution** : elle vit dans une variable
/// locale, `combinedMargin`, et meurt avec la methode. C'est cette resolution qui distingue
/// « aucun gain » de « gain non resolu par cette session », et le produit ne pouvait pas le dire.
///
/// La resolution reste la SOMME des deux marges, comme dans `Compare`. La chaine memoire, elle,
/// utilise la racine de la somme des carres. La somme est plus large, donc plus prudente ; les
/// aligner serait un changement de seuil, et un seuil ne se change pas sans mesure.
/// </summary>
public class AbComparerEvidenceTests
{
    private static BenchmarkResult Passe(
        double avgFps, double marge, double p1Low = 90, double? partGardee = 1.0) =>
        new(avgFps, p1Low, 70, new List<double> { 1000.0 / avgFps }, null, marge, 3,
            HygieneKeptFraction: partGardee);

    [Fact]
    public void Deux_passes_qui_n_ont_pas_vecu_la_meme_chose_ne_se_comparent_PAS()
    {
        // La condition d'invalidation la plus concrete, et elle est OBSERVEE, pas inferee :
        // l'hygiene a retire 2 % de la passe avant et 45 % de la passe apres. Le joueur a
        // alt-tabbe pendant la moitie de la seconde mesure. Comparer les deux revient a comparer
        // deux sessions differentes -- et l'ancien booleen aurait rendu un verdict sans broncher.
        var preuve = AbComparer.Evidence(Passe(100, 2, partGardee: 0.98), Passe(130, 2, partGardee: 0.55));

        preuve.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.Invalid);
        preuve.Game.Preuve.InvalidReason.Should().Contain("exposition");
        preuve.GainEnJeuProuve.Should().BeFalse("+30 % ne vaut rien entre deux sessions differentes");
    }

    [Fact]
    public void Une_passe_PresentMon_et_une_passe_DXGI_ne_se_comparent_PAS()
    {
        // Le repli DXGI ne passe ni par l'hygiene ni par l'exposition : sans cette garde, une passe
        // PresentMon face a une passe DXGI rendait une regression prouvee, et le moteur annulait sur elle.
        var avant = Passe(140, 2, partGardee: 1.0);
        var apres = Passe(110, 2, partGardee: null) with { Instrument = "DXGI" };

        var preuve = AbComparer.Evidence(avant, apres);

        preuve.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.Invalid);
        AbComparer.Evidence(avant with { Instrument = "DXGI" }, apres).Game!.Preuve.Outcome
            .Should().Be(EvidenceOutcome.ProvenRegression, "deux passes DXGI se comparent entre elles");
        var verdict = AbComparer.Compare(avant, apres);
        verdict.IsMeasurableGain.Should().BeFalse();
        verdict.SummaryKey.Should().Be("Scan.Ab.NoMeasurableGain", "entre deux instruments, ni gain ni régression ne se lisent");
    }

    [Fact]
    public void Deux_passes_EGALEMENT_amputees_restent_comparables()
    {
        // Le controle qui empeche la garde d'etre trop zelee : si les deux passes ont perdu
        // autant, elles ont vecu la meme chose, et l'ecart entre elles garde son sens.
        var preuve = AbComparer.Evidence(Passe(100, 2, partGardee: 0.55), Passe(130, 2, partGardee: 0.57));

        preuve.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenGain);
    }

    [Fact]
    public void Une_exposition_INCONNUE_ne_declenche_aucune_invalidation()
    {
        // `null` = l'hygiene n'a pas rendu compte (mesure ancienne, rejeu hors ligne). Unknown ne
        // deduit rien -- ni « comparable », ni « invalide » : on juge sur ce qu'on a.
        var preuve = AbComparer.Evidence(Passe(100, 2, partGardee: null), Passe(120, 2, partGardee: null));

        preuve.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenGain);
    }

    [Fact]
    public void Un_gain_au_dessus_de_la_marge_est_un_GAIN_EN_JEU_PROUVE()
    {
        // +20 fps sur 100, marges 2 + 2 = 4 fps soit 4 % : largement resolu.
        var preuve = AbComparer.Evidence(Passe(100, 2), Passe(120, 2));

        preuve.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenGain);
        preuve.GainEnJeuProuve.Should().BeTrue();
        preuve.Game.Preuve.DeltaPct.Should().BeApproximately(20.0, 0.01);
        preuve.Game.Preuve.ResolutionPct.Should().BeApproximately(4.0, 0.01);
    }

    [Fact]
    public void Une_perte_au_dessus_de_la_marge_est_une_REGRESSION_PROUVEE()
    {
        var preuve = AbComparer.Evidence(Passe(100, 2), Passe(85, 2));

        preuve.RegressionEnJeuProuvee.Should().BeTrue();
        preuve.GainEnJeuProuve.Should().BeFalse();
    }

    [Fact]
    public void Le_cas_REEL_de_la_machine_de_reference_ecart_sous_le_plancher()
    {
        // Le +2,7 % du GPU, vu en jeu, avec la resolution reelle d'une session de rejeu.
        // Un simple booleen dirait « pas de gain » ; c'est faux, rien n'a pu etre resolu.
        var preuve = AbComparer.Evidence(Passe(137.2, 3.3), Passe(140.9, 3.3));

        preuve.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange);
        // Et l'ecart est CONSERVE, avec ce qu'il aurait fallu pour le prouver.
        preuve.Game.Preuve.DeltaPct.Should().BeApproximately(2.70, 0.05);
        preuve.Game.Preuve.ResolutionPct.Should().BeApproximately(4.81, 0.05);
    }

    [Fact]
    public void Une_marge_INCONNUE_ne_laisse_rien_affirmer()
    {
        // `InterRunMarginOfError` vaut +∞ sous 3 passes. Le produit avait alors un booleen `false`,
        // indistinguable d'un vrai « pas de gain ».
        var preuve = AbComparer.Evidence(
            Passe(100, double.PositiveInfinity), Passe(200, double.PositiveInfinity));

        preuve.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange);
        preuve.QuelqueChoseDeProuve.Should().BeFalse("+100 % ne prouve rien si on ignore le bruit");
    }

    [Fact]
    public void Une_passe_AVANT_sans_FPS_rend_l_experience_INVALIDE_pas_neutre()
    {
        // Diviser par zero produirait un pourcentage infini, donc un verdict au hasard. Il n'y a
        // pas de reference : l'experience ne tient pas, et ca ne s'appelle pas « pas de changement ».
        var preuve = AbComparer.Evidence(Passe(0, 2), Passe(120, 2));

        preuve.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.Invalid);
        preuve.Game.Preuve.InvalidReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Le_verdict_booleen_historique_reste_d_accord_avec_la_preuve()
    {
        // Tant que les deux coexistent, ils ne doivent pas se contredire : un gain prouve par le
        // nouveau modele doit rester un gain pour l'ancien, sinon l'un des deux ment.
        foreach (var (avant, apres) in new[]
                 {
                     (Passe(100, 2), Passe(120, 2)),
                     (Passe(100, 2), Passe(85, 2)),
                     (Passe(137.2, 3.3), Passe(140.9, 3.3)),
                 })
        {
            var ancien = AbComparer.Compare(avant, apres);
            var nouveau = AbComparer.Evidence(avant, apres);

            if (nouveau.GainEnJeuProuve)
                ancien.IsMeasurableGain.Should().BeTrue(
                    $"avg {avant.AvgFps} -> {apres.AvgFps} : les deux verdicts doivent concorder");
        }
    }
}
