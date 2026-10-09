using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Hardware;

// -----------------------------------------------------------------------
// R2 GPU bench — GpuBench: turns one deterministic compute run into a repeatable,
// comparable RESULT (throughput score + stable/unstable + checksum-ok), for the
// tools/r2bench A/B. Unit-tested with a FAKE runner — the score math, the verdict
// mapping, and the checksum-ok flag are all covered without a GPU.
//
// Score metric = iterations completed per second (ElementCount * Iterations / sec):
// deterministic workload, so the only thing that moves the score is GPU clock/throughput.
// -----------------------------------------------------------------------
public sealed class GpuBenchTests
{
    private static readonly GpuWorkloadSpec Spec = GpuBenchWorkload.Default;
    private static readonly ulong KnownChecksum = GpuBenchWorkload.ComputeKnownChecksum(Spec);

    private sealed class FakeRunner : IGpuComputeRunner
    {
        private readonly Result<GpuComputeOutcome> _r;
        public FakeRunner(Result<GpuComputeOutcome> r) => _r = r;
        public Result<GpuComputeOutcome> Run(GpuWorkloadSpec spec, TimeSpan duration, CancellationToken ct) => _r;
    }

    private static GpuBench Build(Result<GpuComputeOutcome> outcome)
        => new(new FakeRunner(outcome), Spec);

    [Fact]
    public void Healthy_correct_run_is_stable_checksum_ok_with_positive_score()
    {
        // 256 iterations over the default element count in 2000 ms.
        var outcome = new GpuComputeOutcome(GpuDeviceStatus.Healthy, KnownChecksum, 256, 2000);
        var bench = Build(Result<GpuComputeOutcome>.Ok(outcome));

        var r = bench.Run(0, TimeSpan.FromSeconds(3), default);

        r.IsOk.Should().BeTrue();
        r.Value!.Stability.Should().Be(StabilityOutcome.Stable);
        r.Value.ChecksumOk.Should().BeTrue();
        // score = ElementCount * IterationsCompleted / seconds = 1048576*256 / 2.0
        var expected = (double)Spec.ElementCount * 256 / 2.0;
        r.Value.ElementIterationsPerSecond.Should().BeApproximately(expected, expected * 1e-9);
        r.Value.Checksum.Should().Be(KnownChecksum);
    }

    [Fact]
    public void Score_is_deterministic_for_equal_work_and_time()
    {
        var outcome = new GpuComputeOutcome(GpuDeviceStatus.Healthy, KnownChecksum, 256, 1000);
        var a = Build(Result<GpuComputeOutcome>.Ok(outcome)).Run(0, TimeSpan.FromSeconds(3), default);
        var b = Build(Result<GpuComputeOutcome>.Ok(outcome)).Run(0, TimeSpan.FromSeconds(3), default);

        a.Value!.ElementIterationsPerSecond.Should().Be(b.Value!.ElementIterationsPerSecond);
    }

    [Fact]
    public void Wrong_checksum_is_unstable_and_checksum_not_ok()
    {
        var outcome = new GpuComputeOutcome(GpuDeviceStatus.Healthy, KnownChecksum + 1, 256, 1000);
        var r = Build(Result<GpuComputeOutcome>.Ok(outcome)).Run(0, TimeSpan.FromSeconds(3), default);

        r.IsOk.Should().BeTrue();
        r.Value!.Stability.Should().Be(StabilityOutcome.Unstable);
        r.Value.ChecksumOk.Should().BeFalse();
    }

    [Fact]
    public void Device_lost_is_unstable()
    {
        var outcome = new GpuComputeOutcome(GpuDeviceStatus.DeviceLost, 0, 0, 10);
        var r = Build(Result<GpuComputeOutcome>.Ok(outcome)).Run(0, TimeSpan.FromSeconds(3), default);

        r.Value!.Stability.Should().Be(StabilityOutcome.Unstable);
        r.Value.ChecksumOk.Should().BeFalse();
    }

    [Fact]
    public void Runner_error_is_inconclusive_could_not_run()
    {
        var outcome = new GpuComputeOutcome(GpuDeviceStatus.Error, 0, 0, 0);
        var r = Build(Result<GpuComputeOutcome>.Ok(outcome)).Run(0, TimeSpan.FromSeconds(3), default);

        r.Value!.Stability.Should().Be(StabilityOutcome.Inconclusive);
        r.Value.ChecksumOk.Should().BeFalse();
    }

    [Fact]
    public void Runner_failure_is_inconclusive_and_never_throws()
    {
        var r = Build(Result<GpuComputeOutcome>.Fail(ErrorCodes.TweakSdkUnavailable, "no D3D11"))
            .Run(0, TimeSpan.FromSeconds(3), default);

        r.IsOk.Should().BeTrue();
        r.Value!.Stability.Should().Be(StabilityOutcome.Inconclusive);
    }
}

public sealed class GpuBenchWorkloadTests
{
    [Fact]
    public void Known_checksum_is_deterministic_run_to_run()
    {
        var spec = GpuBenchWorkload.Default;
        var a = GpuBenchWorkload.ComputeKnownChecksum(spec);
        var b = GpuBenchWorkload.ComputeKnownChecksum(spec);
        a.Should().Be(b);
    }

    [Fact]
    public void Different_seed_changes_the_checksum()
    {
        var s1 = GpuBenchWorkload.Default;
        var s2 = s1 with { Seed = s1.Seed + 1 };
        GpuBenchWorkload.ComputeKnownChecksum(s1).Should().NotBe(GpuBenchWorkload.ComputeKnownChecksum(s2));
    }
}
