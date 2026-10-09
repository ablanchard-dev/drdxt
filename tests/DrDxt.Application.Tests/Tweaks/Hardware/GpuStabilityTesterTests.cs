using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Hardware;

// -----------------------------------------------------------------------
// R2b — GpuStabilityTester orchestration (the safety-critical fail-safe logic),
// unit-tested with a FAKE load probe (no GPU needed). The real D3D11 GPU-load probe
// is injected and smoke-validated separately.
//
// INVARIANT: Never report Stable unless the brief GPU load completed with a healthy
// device. Any probe failure / unknown result / thrown exception is treated
// conservatively as Inconclusive — which the undervolt tweak treats as "revert".
// This is the whole point: a stability test that can't be sure must never green-light
// an unverified undervolt.
// -----------------------------------------------------------------------
public sealed class GpuStabilityTesterTests
{
    private sealed class FakeProbe : IGpuLoadProbe
    {
        private readonly Func<Result<GpuLoadResult>> _fn;
        public FakeProbe(Func<Result<GpuLoadResult>> fn) => _fn = fn;
        public Task<Result<GpuLoadResult>> RunBriefLoadAsync(int gpuIndex, TimeSpan duration, CancellationToken ct)
            => Task.FromResult(_fn());   // _fn may throw to simulate a probe blowing up
    }

    private static GpuStabilityTester Build(Func<Result<GpuLoadResult>> probe) => new(new FakeProbe(probe));

    [Fact]
    public async Task Healthy_load_reports_stable()
    {
        var r = await Build(() => Result<GpuLoadResult>.Ok(GpuLoadResult.CompletedDeviceHealthy)).RunAsync(0, default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(StabilityOutcome.Stable);
    }

    [Fact]
    public async Task Device_lost_reports_unstable()
    {
        var r = await Build(() => Result<GpuLoadResult>.Ok(GpuLoadResult.DeviceLost)).RunAsync(0, default);

        r.Value.Should().Be(StabilityOutcome.Unstable);
    }

    [Fact]
    public async Task Could_not_run_reports_inconclusive()
    {
        var r = await Build(() => Result<GpuLoadResult>.Ok(GpuLoadResult.CouldNotRun)).RunAsync(0, default);

        r.Value.Should().Be(StabilityOutcome.Inconclusive);
    }

    [Fact]
    public async Task Probe_failure_is_inconclusive_never_stable()
    {
        var r = await Build(() => Result<GpuLoadResult>.Fail(ErrorCodes.TweakSdkUnavailable, "no D3D")).RunAsync(0, default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(StabilityOutcome.Inconclusive);
    }

    [Fact]
    public async Task Probe_exception_is_inconclusive_and_never_throws()
    {
        var tester = Build(() => throw new InvalidOperationException("device blew up under load"));

        var r = await tester.RunAsync(0, default);   // must not throw

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(StabilityOutcome.Inconclusive);
    }
}
