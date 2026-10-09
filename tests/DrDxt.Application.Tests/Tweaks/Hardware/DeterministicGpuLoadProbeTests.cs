using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Hardware;

// -----------------------------------------------------------------------
// R2 GPU bench — DeterministicGpuLoadProbe (the pure decision logic over the thin
// IGpuComputeRunner D3D11 seam), unit-tested with a FAKE runner (no GPU needed).
//
// Decision contract (fail-safe — never falsely Stable):
//   * Healthy device + checksum == known answer        → CompletedDeviceHealthy
//   * DeviceLost (TDR)                                  → DeviceLost
//   * Healthy device but checksum != known answer       → DeviceLost  (silent corruption;
//                                                          "no TDR ≠ stable")
//   * runner reports Error / fails / throws            → CouldNotRun  (probe never throws)
//
// The real D3D11 IGpuComputeRunner is the only uncovered boundary (live smoke).
// -----------------------------------------------------------------------
public sealed class DeterministicGpuLoadProbeTests
{
    private static readonly GpuWorkloadSpec Spec = GpuBenchWorkload.Default;
    private static readonly ulong KnownChecksum = GpuBenchWorkload.ComputeKnownChecksum(Spec);

    private sealed class FakeRunner : IGpuComputeRunner
    {
        private readonly Func<GpuWorkloadSpec, Result<GpuComputeOutcome>> _fn;
        public FakeRunner(Func<GpuWorkloadSpec, Result<GpuComputeOutcome>> fn) => _fn = fn;
        public Result<GpuComputeOutcome> Run(GpuWorkloadSpec spec, TimeSpan duration, CancellationToken ct)
            => _fn(spec);   // _fn may throw to simulate the device blowing up
    }

    private static DeterministicGpuLoadProbe Build(Func<GpuWorkloadSpec, Result<GpuComputeOutcome>> runner)
        => new(new FakeRunner(runner), Spec);

    private static GpuComputeOutcome Healthy(ulong checksum, int iters = 256, double ms = 1000)
        => new(GpuDeviceStatus.Healthy, checksum, iters, ms);

    [Fact]
    public async Task Healthy_with_correct_checksum_reports_completed_healthy()
    {
        var probe = Build(_ => Result<GpuComputeOutcome>.Ok(Healthy(KnownChecksum)));

        var r = await probe.RunBriefLoadAsync(0, TimeSpan.FromSeconds(3), default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(GpuLoadResult.CompletedDeviceHealthy);
    }

    [Fact]
    public async Task Device_lost_reports_device_lost()
    {
        var probe = Build(_ => Result<GpuComputeOutcome>.Ok(
            new GpuComputeOutcome(GpuDeviceStatus.DeviceLost, 0, 0, 50)));

        var r = await probe.RunBriefLoadAsync(0, TimeSpan.FromSeconds(3), default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(GpuLoadResult.DeviceLost);
    }

    [Fact]
    public async Task Healthy_but_wrong_checksum_reports_device_lost_silent_corruption()
    {
        // No TDR, device "healthy", but the output is wrong → silent instability.
        var probe = Build(_ => Result<GpuComputeOutcome>.Ok(Healthy(KnownChecksum ^ 0xDEAD)));

        var r = await probe.RunBriefLoadAsync(0, TimeSpan.FromSeconds(3), default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(GpuLoadResult.DeviceLost);   // "no TDR ≠ stable"
    }

    [Fact]
    public async Task Runner_error_reports_could_not_run()
    {
        var probe = Build(_ => Result<GpuComputeOutcome>.Ok(
            new GpuComputeOutcome(GpuDeviceStatus.Error, 0, 0, 0)));

        var r = await probe.RunBriefLoadAsync(0, TimeSpan.FromSeconds(3), default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(GpuLoadResult.CouldNotRun);
    }

    [Fact]
    public async Task Runner_failure_result_reports_could_not_run()
    {
        var probe = Build(_ => Result<GpuComputeOutcome>.Fail(ErrorCodes.TweakSdkUnavailable, "no D3D11"));

        var r = await probe.RunBriefLoadAsync(0, TimeSpan.FromSeconds(3), default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(GpuLoadResult.CouldNotRun);
    }

    [Fact]
    public async Task Runner_exception_is_could_not_run_and_never_throws()
    {
        var probe = Build(_ => throw new InvalidOperationException("device removed mid-dispatch"));

        var r = await probe.RunBriefLoadAsync(0, TimeSpan.FromSeconds(3), default);   // must not throw

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(GpuLoadResult.CouldNotRun);
    }

    [Fact]
    public async Task Probe_passes_its_configured_spec_to_the_runner()
    {
        GpuWorkloadSpec? seen = null;
        var probe = Build(s => { seen = s; return Result<GpuComputeOutcome>.Ok(Healthy(KnownChecksum)); });

        await probe.RunBriefLoadAsync(0, TimeSpan.FromSeconds(3), default);

        seen.Should().Be(Spec);
    }
}
