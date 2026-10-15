using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Orchestration de GpuStabilityTester (la logique de sûreté intégrée, critique pour la
// sécurité), testée avec une fausse sonde de charge (aucune carte requise). La vraie sonde
// de charge D3D11 est injectée et validée en fumée à part.
//
// Invariant : ne jamais rapporter Stable sauf si la brève charge GPU s'est achevée sur un
// périphérique sain. Toute panne de sonde, résultat inconnu ou exception est traité par
// prudence comme Inconclusive, que le levier d'undervolt traite comme « retour arrière ».
// C'est tout l'enjeu : un test de stabilité qui ne peut pas être sûr ne doit jamais
// donner le feu vert à un undervolt non vérifié.
// -----------------------------------------------------------------------
public sealed class GpuStabilityTesterTests
{
    private sealed class FakeProbe : IGpuLoadProbe
    {
        private readonly Func<Result<GpuLoadResult>> _fn;
        public FakeProbe(Func<Result<GpuLoadResult>> fn) => _fn = fn;
        public Task<Result<GpuLoadResult>> RunBriefLoadAsync(int gpuIndex, TimeSpan duration, CancellationToken ct)
            => Task.FromResult(_fn());   // _fn peut lever pour simuler une sonde qui explose
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

        var r = await tester.RunAsync(0, default);   // ne doit pas lever

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(StabilityOutcome.Inconclusive);
    }
}
