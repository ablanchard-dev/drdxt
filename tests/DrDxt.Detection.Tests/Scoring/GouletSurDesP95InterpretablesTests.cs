using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Scoring;

/// <summary>Le classifieur de goulot suit la même règle que l'écran : un p95 sous vingt échantillons se confond avec
/// le maximum, il ne décide rien. Sans elle, cinq échantillons suffisaient à supprimer XMP, X3D ou tout gain de FPS.</summary>
public class GouletSurDesP95InterpretablesTests
{
    private static BenchmarkResult Base() =>
        new BenchmarkResult(143, 140, 138, Enumerable.Repeat(7.0, 3000).ToList(), null, 1, Runs: 3)
        { GpuBusyShare = 0.96, GpuWaitRatio = 0.02 };

    [Fact]
    public void Une_erreur_d_animation_sur_cinq_echantillons_ne_fait_pas_un_probleme_de_cadence()
        => BottleneckClassifier.Classify(Base() with { AnimationErrorMsP95 = 6, AnimationErrorSamples = 5 })
            .State.Should().Be(BottleneckState.GpuBound);

    [Fact]
    public void Une_attente_d_affichage_sur_cinq_echantillons_ne_fait_pas_un_plafond()
        => BottleneckClassifier.Classify(Base() with { GpuBusyShare = 0.85, PresentStallMsP95 = 3, PresentStallSamples = 5 })
            .State.Should().NotBe(BottleneckState.CappedOrVsyncLimited);

    [Fact]
    public void Sur_assez_d_echantillons_la_cadence_compte_toujours()
        => BottleneckClassifier.Classify(Base() with { AnimationErrorMsP95 = 6, AnimationErrorSamples = 3000 })
            .State.Should().Be(BottleneckState.FramePacing);
}
