using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Banc GPU : DeterministicGpuLoadProbe (la logique de décision pure au-dessus de la mince
// couture D3D11 IGpuComputeRunner), testée avec un faux exécutant (aucune carte requise).
//
// Contrat de décision (à sûreté intégrée, jamais faussement Stable) :
//   * périphérique sain + somme de contrôle == réponse connue  → CompletedDeviceHealthy
//   * DeviceLost (TDR)                                          → DeviceLost
//   * périphérique sain mais somme de contrôle != réponse connue → DeviceLost (corruption
//                                                                  silencieuse ; « pas de TDR ≠ stable »)
//   * l'exécutant rapporte Error / échoue / lève                → CouldNotRun (la sonde ne lève jamais)
//
// Le vrai IGpuComputeRunner D3D11 est la seule frontière non couverte (fumée sur banc).
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
            => _fn(spec);   // _fn peut lever pour simuler l'explosion du périphérique
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
        // Pas de TDR, périphérique « sain », mais la sortie est fausse → instabilité silencieuse.
        var probe = Build(_ => Result<GpuComputeOutcome>.Ok(Healthy(KnownChecksum ^ 0xDEAD)));

        var r = await probe.RunBriefLoadAsync(0, TimeSpan.FromSeconds(3), default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(GpuLoadResult.DeviceLost);   // « pas de TDR ≠ stable »
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

        var r = await probe.RunBriefLoadAsync(0, TimeSpan.FromSeconds(3), default);   // ne doit pas lever

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
