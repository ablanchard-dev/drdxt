using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Hardware;

// -----------------------------------------------------------------------
// P1 — GpuBenchSession: the deterministic GPU perf-METER. Runs the deterministic
// GpuBench N times and aggregates the comparable throughput scores into a mean + an
// inter-run margin (same NS-6 honesty as the frametime A/B: +Infinity under 3 runs),
// plus AllStable (every run checksum-ok / no TDR). GpuBenchAb turns two aggregates
// (stock vs candidate) into a clean verdict — a gain must CLEAR the combined margin.
// Fully unit-tested with a sequence fake runner — no GPU.
// -----------------------------------------------------------------------
public sealed class GpuBenchSessionTests
{
    private static readonly GpuWorkloadSpec Spec = GpuBenchWorkload.Default;
    private static readonly ulong Known = GpuBenchWorkload.ComputeKnownChecksum(Spec);

    private sealed class SequenceRunner : IGpuComputeRunner
    {
        private readonly Queue<Result<GpuComputeOutcome>> _q;
        public SequenceRunner(IEnumerable<Result<GpuComputeOutcome>> items) => _q = new(items);
        public Result<GpuComputeOutcome> Run(GpuWorkloadSpec spec, TimeSpan d, CancellationToken ct)
            => _q.Count > 0 ? _q.Dequeue() : Result<GpuComputeOutcome>.Fail(ErrorCodes.TweakSdkUnavailable, "drained");
    }

    private static Result<GpuComputeOutcome> Stable(int iters, double ms)
        => Result<GpuComputeOutcome>.Ok(new GpuComputeOutcome(GpuDeviceStatus.Healthy, Known, iters, ms));
    private static Result<GpuComputeOutcome> Unstable()
        => Result<GpuComputeOutcome>.Ok(new GpuComputeOutcome(GpuDeviceStatus.DeviceLost, 0, 0, 10));

    private static GpuBenchSession Session(params Result<GpuComputeOutcome>[] outcomes)
        => new(new GpuBench(new SequenceRunner(outcomes), Spec));

    private static double ScoreOf(int iters, double ms) => (double)Spec.ElementCount * iters / (ms / 1000.0);

    [Fact]
    public void Three_stable_runs_give_mean_and_finite_margin()
    {
        var r = Session(Stable(256, 1000), Stable(256, 1010), Stable(256, 990))
            .Measure(0, TimeSpan.FromSeconds(1), 3, default);

        r.IsOk.Should().BeTrue();
        r.Value!.StableRuns.Should().Be(3);
        r.Value.AllStable.Should().BeTrue();
        r.Value.MeanScore.Should().BeApproximately(
            (ScoreOf(256, 1000) + ScoreOf(256, 1010) + ScoreOf(256, 990)) / 3.0, 1.0);
        double.IsFinite(r.Value.MarginOfError).Should().BeTrue();
        r.Value.MarginOfError.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Fewer_than_three_stable_runs_has_infinite_margin()
    {
        var r = Session(Stable(256, 1000), Stable(256, 1000)).Measure(0, TimeSpan.FromSeconds(1), 2, default);
        r.Value!.StableRuns.Should().Be(2);
        double.IsPositiveInfinity(r.Value.MarginOfError).Should().BeTrue();
    }

    [Fact]
    public void An_unstable_run_sets_not_all_stable_and_is_excluded_from_the_score()
    {
        var r = Session(Stable(256, 1000), Unstable(), Stable(256, 1000)).Measure(0, TimeSpan.FromSeconds(1), 3, default);
        r.Value!.AllStable.Should().BeFalse();
        r.Value.StableRuns.Should().Be(2);
    }

    [Fact]
    public void No_stable_run_fails_clean()
    {
        var r = Session(Unstable(), Unstable(), Unstable()).Measure(0, TimeSpan.FromSeconds(1), 3, default);
        r.IsOk.Should().BeFalse();
    }

    // --- A/B verdict (a gain must clear the COMBINED inter-run margin; unstable = revert) ---
    private static GpuBenchAggregate Agg(double mean, double margin, bool stable = true) => new(mean, margin, 3, stable);

    [Fact]
    public void Ab_gain_when_delta_clears_combined_margin()
        => GpuBenchAb.Compare(Agg(100, 2), Agg(110, 2)).Should().Be(GpuBenchAbVerdict.Gain);

    [Fact]
    public void Ab_within_noise_is_no_measurable_change()
        => GpuBenchAb.Compare(Agg(100, 5), Agg(103, 5)).Should().Be(GpuBenchAbVerdict.NoMeasurableChange);

    [Fact]
    public void Ab_regression_when_delta_clears_margin_downward()
        => GpuBenchAb.Compare(Agg(110, 2), Agg(100, 2)).Should().Be(GpuBenchAbVerdict.Regression);

    [Fact]
    public void Ab_unstable_candidate_is_unstable_verdict()
        => GpuBenchAb.Compare(Agg(100, 2), Agg(120, 2, stable: false)).Should().Be(GpuBenchAbVerdict.Unstable);
}
