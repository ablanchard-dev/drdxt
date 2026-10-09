using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>Phase 18 — la pression du thread de jeu, branchee dans le diagnostic de l'App.</summary>
public class PressionThreadsTests
{
    [Theory]
    [InlineData(0.95, 12, GameThreadPressure.Observee)]
    [InlineData(0.90, 12, GameThreadPressure.Observee)]
    [InlineData(0.50, 12, GameThreadPressure.Aucune)]
    [InlineData(0.95, 0, GameThreadPressure.Unknown)]    // rien de lisible : pas « aucune pression »
    public void Classer(double part, int vus, GameThreadPressure attendu)
        => PressionThreads.Classer(part, vus).Should().Be(attendu);

    [Fact]
    public void Non_lu_n_est_jamais_aucune_pression()
        => PressionThreads.Classer(null, 0).Should().Be(GameThreadPressure.Unknown);

    private static BottleneckVerdict Mixte => new(BottleneckState.MixedGpuLeaning, BottleneckConfidence.High, "motif", false);

    [Fact]
    public void Un_thread_de_jeu_sature_fait_remonter_la_famille_CPU_en_tete()
    {
        // Le branchement qui compte : sans lui, le selecteur ne voyait jamais la famille CPU.
        var diagnostic = DiagnosticDeScan.Construire(Mixte, null, null, null, PressionThreads.Classer(0.95, 12));

        CandidateSelector.ClasserFamilles(diagnostic)[0].Famille.Should().Be(CandidateFamily.Cpu);
    }

    [Fact]
    public void Sans_observation_la_famille_CPU_ne_remonte_pas()
        => CandidateSelector.ClasserFamilles(DiagnosticDeScan.Construire(Mixte, null, null, null))
            .Should().NotContain(f => f.Famille == CandidateFamily.Cpu);
}
