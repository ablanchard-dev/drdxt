using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Un seul sens avant toute preuve : delta positif = AMELIORATION.
///
/// C'est la classe de bug la plus dangereuse du produit, parce qu'elle est SILENCIEUSE et
/// qu'elle inverse la decision. Les FPS se lisent « plus haut = mieux » ; les frametimes se lisent
/// « plus BAS = mieux ». Un p95 qui passe de 12 ms a 10 ms est un progres — et une chaine qui
/// oublie l'inversion l'appellerait une degradation de 17 %, annulerait le reglage, et le dirait
/// avec la meme assurance qu'un vrai verdict.
///
/// Aucun test ne l'aurait attrape : la boucle tourne, les chiffres sortent, le rapport est
/// coherent. Seul un controle qui connait le SENS attendu peut voir l'erreur.
///
/// La normalisation vit dans <see cref="GameMetricSet.Valeur"/>, en un seul endroit. Ces tests
/// interdisent qu'une seconde apparaisse ailleurs.
/// </summary>
public class MetricDirectionTests
{
    private static GameMetrics M(double avg, double p1low, double p95Ms) =>
        new(avg, p1low, null, p95Ms);

    [Fact]
    public void UN_p95_FRAMETIME_PLUS_COURT_VAUT_PLUS_lu_dans_le_sens_du_produit()
    {
        // 10 ms est meilleur que 12 ms. La valeur normalisee doit donc etre plus GRANDE.
        var lent = GameMetricSet.Valeur(M(140, 100, 12.0), GameMetricKind.P95Frametime);
        var rapide = GameMetricSet.Valeur(M(140, 100, 10.0), GameMetricKind.P95Frametime);

        rapide.Should().BeGreaterThan(lent,
            "un frametime plus court est un progres, et le produit doit le lire comme tel");
    }

    [Fact]
    public void Les_FPS_gardent_leur_sens_naturel()
    {
        GameMetricSet.Valeur(M(150, 100, 10), GameMetricKind.Moyenne)
            .Should().BeGreaterThan(GameMetricSet.Valeur(M(140, 100, 10), GameMetricKind.Moyenne));

        GameMetricSet.Valeur(M(140, 110, 10), GameMetricKind.UnPourCentLow)
            .Should().BeGreaterThan(GameMetricSet.Valeur(M(140, 100, 10), GameMetricKind.UnPourCentLow));
    }

    /// <summary>Le contrôle POSITIF de bout en bout : la moyenne monte et le p95 en ms baisse.
    /// Les deux doivent ressortir `ProvenGain`, pas l'une contre l'autre.</summary>
    [Fact]
    public void UNE_AMELIORATION_REELLE_donne_ProvenGain_sur_les_DEUX_grandeurs()
    {
        var profil = Exercer(avgB: 147.0, p95MsB: 9.0);   // +5 % de moyenne, p95 de 10 ms a 9 ms

        Preuve(profil, GameMetricKind.Moyenne).Outcome.Should().Be(EvidenceOutcome.ProvenGain);
        Preuve(profil, GameMetricKind.P95Frametime).Outcome.Should().Be(EvidenceOutcome.ProvenGain);
        Preuve(profil, GameMetricKind.P95Frametime).DeltaPct.Should().BePositive(
            "un p95 raccourci est un delta POSITIF : c'est toute la question de ce fichier");
    }

    /// <summary>Le contrôle NEGATIF, symetrique : la moyenne baisse et le p95 en ms monte.</summary>
    [Fact]
    public void UNE_DEGRADATION_REELLE_donne_ProvenRegression_sur_les_DEUX()
    {
        var profil = Exercer(avgB: 133.0, p95MsB: 11.0);   // -5 % de moyenne, p95 de 10 ms a 11 ms

        Preuve(profil, GameMetricKind.Moyenne).Outcome.Should().Be(EvidenceOutcome.ProvenRegression);
        Preuve(profil, GameMetricKind.P95Frametime).Outcome.Should().Be(EvidenceOutcome.ProvenRegression);
        Preuve(profil, GameMetricKind.P95Frametime).DeltaPct.Should().BeNegative();
    }

    /// <summary>Le piege exact que l'inversion produirait : un p95 AMELIORE ne doit jamais
    /// faire annuler un reglage. Si ce test tombe, le produit annule ce qu'il cherche.</summary>
    [Fact]
    public void Un_p95_AMELIORE_ne_peut_JAMAIS_provoquer_un_revert_pour_regression()
    {
        var profil = Exercer(avgB: 147.0, p95MsB: 9.0);
        var decision = KeepPolicy.DecideParMetrique(
            profil.Results.Single().Metriques!, OptimizationGoal.Competitive);

        decision.RaisonCle.Should().NotBe(KeepPolicy.CleRegression);
        decision.Keep.Should().BeTrue("la fluidite p95 est prouvee et rien ne s'est degrade");
    }

    // ── mecanique ───────────────────────────────────────────────────────────────────────────

    private static Evidence Preuve(AutopilotProfile p, GameMetricKind k) =>
        p.Results.Single().Metriques!.Single(m => m.Metrique == k).Preuve;

    /// <summary>Une boucle complete avec un candidat qui change les metriques quand il est
    /// applique. Bruit minuscule et deterministe : ce fichier teste le SENS, pas la sensibilite.</summary>
    private static AutopilotProfile Exercer(double avgB, double p95MsB)
    {
        var c = new CandidatFake();
        var i = 0;
        return AutopilotEngine.Run(
            new IAutopilotCandidate[] { c },
            () =>
            {
                var jitter = (i++ % 2 == 0 ? 1 : -1) * 0.05;
                return c.Applique
                    ? new GameMetrics(avgB + jitter, avgB * 0.7 + jitter, null, p95MsB)
                    : new GameMetrics(140.0 + jitter, 98.0 + jitter, null, 10.0);
            },
            passesAa: 3, stable: null, but: OptimizationGoal.Competitive, alternancesMax: 8);
    }

    private sealed class CandidatFake : IAutopilotCandidate
    {
        public bool Applique { get; private set; }
        public CandidateDescriptor Descriptor { get; } = new(
            "essai", CandidateKind.AutoHot, RiskLevel: 0, NeedsStability: false, new[] { "fps.moyen" });

        public Result Apply() { Applique = true; return Result.Ok(); }
        public Result Revert() { Applique = false; return Result.Ok(); }
        public Result<bool> ReadBack() => Result<bool>.Ok(Applique);
    }
}
