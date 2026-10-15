using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests.Measurement;

public class BenchmarkResultTests
{
    private static BenchmarkResult Bench(double avgFps, double? gpuBusyMs) =>
        new(avgFps, 0, 0, Array.Empty<double>(), null, 0, 1, gpuBusyMs);

    private static BenchmarkResult WithFrames(params double[] frameTimesMs) =>
        new(0, 0, 0, frameTimesMs, null, 0, 1);

    [Fact]
    public void Gpu_busy_percent_is_busy_over_frametime()
        // 7 ms d'occupation à 100 fps (10 ms/image) → 70 %.
        => Bench(100, 7).GpuBusyPercent.Should().BeApproximately(70.0, 1e-9);

    [Fact]
    public void Gpu_busy_percent_null_when_not_measured()
        => Bench(100, null).GpuBusyPercent.Should().BeNull();

    [Fact]
    public void Gpu_busy_percent_null_when_fps_unknown()
        => Bench(0, 7).GpuBusyPercent.Should().BeNull();

    [Fact]
    public void Stutter_percent_is_zero_for_a_smooth_capture()
        // Dix images régulières de 10 ms → aucune ne dépasse 2,5× la moyenne → 0 % du temps en saccade.
        => WithFrames(10, 10, 10, 10, 10, 10, 10, 10, 10, 10)
            .StutterTimePercent.Should().BeApproximately(0.0, 1e-9);

    [Fact]
    public void Stutter_percent_is_share_of_time_in_frames_over_2_5x_the_mean()
        // Neuf images de 10 ms + une pointe de 90 ms : moyenne 18 ms, seuil 45 ms, l'image de 90 ms compte
        // → 90 ms sur 180 ms au total = 50 % du temps de jeu en saccade (facteur 2,5× de CapFrameX).
        => WithFrames(10, 10, 10, 10, 10, 10, 10, 10, 10, 90)
            .StutterTimePercent.Should().BeApproximately(50.0, 1e-9);

    [Fact]
    public void Stutter_percent_ignores_invalid_frames()
        // Les images perdues ou aberrantes (≤0, NaN, ∞) sont filtrées avant le ratio, comme le reste des
        // calculs de temps d'image : elles ne doivent pas corrompre le résultat. Même 50 % que le cas propre ci-dessus.
        => WithFrames(10, 10, 10, 10, 10, 10, 10, 10, 10, 90, 0, double.NaN, double.PositiveInfinity)
            .StutterTimePercent.Should().BeApproximately(50.0, 1e-9);

    [Fact]
    public void Stutter_percent_null_when_no_frames()
        => WithFrames().StutterTimePercent.Should().BeNull();

    // --- Classification du goulot : occupation GPU contre temps d'image (méthode PresentMon d'Intel) ---

    [Fact]
    public void Bottleneck_is_gpu_bound_when_gpu_busy_fills_the_frame()
        // 9,5 ms d'occupation à 100 fps (10 ms/image) → 95 % ≥ 90 → la carte a rythmé l'image.
        => Bench(100, 9.5).Bottleneck.Should().Be(Bottleneck.GpuBound);

    [Fact]
    public void Bottleneck_is_cpu_or_other_bound_when_a_big_share_is_spent_off_gpu()
        // 7 ms d'occupation à 100 fps → 70 % ≤ 75 → une part significative de l'image s'est passée hors carte.
        => Bench(100, 7).Bottleneck.Should().Be(Bottleneck.CpuOrOtherBound);

    [Fact]
    public void Bottleneck_is_indeterminate_in_the_middle_band()
        // 8 ms d'occupation à 100 fps → 80 %, dans la bande 75–90 → on n'affirme rien dans un sens ni dans l'autre.
        => Bench(100, 8).Bottleneck.Should().Be(Bottleneck.Indeterminate);

    [Fact]
    public void Bottleneck_is_indeterminate_when_gpu_busy_unmeasured()
        // Pas de donnée d'occupation GPU → Indeterminate honnête, jamais un GpuBound fabriqué.
        => Bench(100, null).Bottleneck.Should().Be(Bottleneck.Indeterminate);
}
