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
        // 7 ms busy at 100 fps (10 ms/frame) → 70 %.
        => Bench(100, 7).GpuBusyPercent.Should().BeApproximately(70.0, 1e-9);

    [Fact]
    public void Gpu_busy_percent_null_when_not_measured()
        => Bench(100, null).GpuBusyPercent.Should().BeNull();

    [Fact]
    public void Gpu_busy_percent_null_when_fps_unknown()
        => Bench(0, 7).GpuBusyPercent.Should().BeNull();

    [Fact]
    public void Stutter_percent_is_zero_for_a_smooth_capture()
        // Ten even 10 ms frames → none exceeds 2.5× the mean → 0 % of time stuttering.
        => WithFrames(10, 10, 10, 10, 10, 10, 10, 10, 10, 10)
            .StutterTimePercent.Should().BeApproximately(0.0, 1e-9);

    [Fact]
    public void Stutter_percent_is_share_of_time_in_frames_over_2_5x_the_mean()
        // Nine 10 ms frames + one 90 ms spike: mean 18 ms, threshold 45 ms, the 90 ms frame counts
        // → 90 ms of 180 ms total = 50 % of play-time stuttering (CapFrameX 2.5× factor).
        => WithFrames(10, 10, 10, 10, 10, 10, 10, 10, 10, 90)
            .StutterTimePercent.Should().BeApproximately(50.0, 1e-9);

    [Fact]
    public void Stutter_percent_ignores_invalid_frames()
        // Dropped/garbage frames (≤0, NaN, ∞) are filtered before the ratio, like the rest of the
        // frametime maths — they must not corrupt the result. Same 50 % as the clean case above.
        => WithFrames(10, 10, 10, 10, 10, 10, 10, 10, 10, 90, 0, double.NaN, double.PositiveInfinity)
            .StutterTimePercent.Should().BeApproximately(50.0, 1e-9);

    [Fact]
    public void Stutter_percent_null_when_no_frames()
        => WithFrames().StutterTimePercent.Should().BeNull();

    // --- Bottleneck classification (M8): GPU-busy vs frame time (Intel PresentMon methodology) ---

    [Fact]
    public void Bottleneck_is_gpu_bound_when_gpu_busy_fills_the_frame()
        // 9.5 ms busy at 100 fps (10 ms/frame) → 95 % ≥ 90 → the GPU paced the frame.
        => Bench(100, 9.5).Bottleneck.Should().Be(Bottleneck.GpuBound);

    [Fact]
    public void Bottleneck_is_cpu_or_other_bound_when_a_big_share_is_spent_off_gpu()
        // 7 ms busy at 100 fps → 70 % ≤ 75 → a meaningful share of the frame was off-GPU.
        => Bench(100, 7).Bottleneck.Should().Be(Bottleneck.CpuOrOtherBound);

    [Fact]
    public void Bottleneck_is_indeterminate_in_the_middle_band()
        // 8 ms busy at 100 fps → 80 %, inside the 75–90 band → we do not over-claim either way.
        => Bench(100, 8).Bottleneck.Should().Be(Bottleneck.Indeterminate);

    [Fact]
    public void Bottleneck_is_indeterminate_when_gpu_busy_unmeasured()
        // No GPU-busy data → honest Indeterminate, never a fabricated GpuBound.
        => Bench(100, null).Bottleneck.Should().Be(Bottleneck.Indeterminate);
}
