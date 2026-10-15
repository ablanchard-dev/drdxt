using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Adapters;
using FluentAssertions;

namespace DrDxt.Reconciler.Adapters.Tests;

// -----------------------------------------------------------------------
// SurfaceSettingProbe : ISettingProbe qui dispatche selon la surface du descripteur
// (Registry / PowerPlan / Service), en sérialisant vers le même JSON canonique que le
// manifeste stocke en DesiredValueJson. ScheduledTask → « non supporté en V1 ».
// Identifiant inconnu → CanProbe=false + Fail. Les 3 lectures OS brutes sont derrière des
// coutures internes, donc dispatch et sérialisation se testent entièrement avec des faux.
// -----------------------------------------------------------------------
public sealed class SurfaceSettingProbeTests
{
    private const string ReflexId = "latency.reflex.lowLatencyMode";
    private const string PowerId = "fixit.sanePowerPlan";
    private const string ServiceId = "fixit.x3dVCacheService";

    // --- faux des trois coutures de lecture OS brute ---

    private sealed class FakeRegistry : IRawRegistryValueReader
    {
        public Result<string> Result { private get; set; } =
            Core.Result<string>.Ok("0");
        public RegistryDescriptor? LastDescriptor { get; private set; }

        public Result<string> ReadCanonical(RegistryDescriptor descriptor)
        {
            LastDescriptor = descriptor;
            return Result;
        }
    }

    private sealed class FakePowerPlan : IRawPowerPlanReader
    {
        public Result<string> Result { private get; set; } =
            Core.Result<string>.Ok("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public Result<string> ReadActiveSchemeCanonical() => Result;
    }

    private sealed class FakeService : IRawServiceStateReader
    {
        public Result<string> Result { private get; set; } =
            Core.Result<string>.Ok("running");
        public string? LastServiceName { get; private set; }

        public Result<string> ReadStateCanonical(string serviceName)
        {
            LastServiceName = serviceName;
            return Result;
        }
    }

    private static (SurfaceSettingProbe probe, FakeRegistry reg, FakePowerPlan power, FakeService svc) Build()
    {
        var reg = new FakeRegistry();
        var power = new FakePowerPlan();
        var svc = new FakeService();
        var probe = new SurfaceSettingProbe(new DefaultReconcilableTweakCatalog(), reg, power, svc);
        return (probe, reg, power, svc);
    }

    [Fact]
    public void CanProbe_TrueForKnownReadableSurfaces()
    {
        var (probe, _, _, _) = Build();

        probe.CanProbe(ReflexId).Should().BeTrue();
        probe.CanProbe(PowerId).Should().BeTrue();
        probe.CanProbe(ServiceId).Should().BeTrue();
    }

    [Fact]
    public void CanProbe_FalseForUnknownOrUnmappedId()
    {
        var (probe, _, _, _) = Build();

        probe.CanProbe("does.not.exist").Should().BeFalse();
        probe.CanProbe("latency.renderQueueCap").Should().BeFalse(); // porté par DRS, aucune feuille lisible
    }

    [Fact]
    public async Task Registry_DispatchesToRegistryReader_AndReturnsCanonicalValue()
    {
        var (probe, reg, _, _) = Build();
        reg.Result = Result<string>.Ok("2");

        var r = await probe.ReadCurrentValueAsync(ReflexId, default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be("2");
        reg.LastDescriptor!.Name.Should().Be("LowLatencyMode");
    }

    [Fact]
    public async Task PowerPlan_DispatchesToPowerReader_AndReturnsActiveSchemeGuid()
    {
        var (probe, _, power, _) = Build();
        power.Result = Result<string>.Ok("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

        var r = await probe.ReadCurrentValueAsync(PowerId, default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    }

    [Fact]
    public async Task Service_DispatchesToServiceReader_WithTheDescriptorServiceName()
    {
        var (probe, _, _, svc) = Build();
        svc.Result = Result<string>.Ok("stopped");

        var r = await probe.ReadCurrentValueAsync(ServiceId, default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be("stopped");
        svc.LastServiceName.Should().Be("AMD3DVCacheSvc");
    }

    [Fact]
    public async Task RawReadFailure_FailsClean()
    {
        var (probe, reg, _, _) = Build();
        reg.Result = Result<string>.Fail(ErrorCodes.RegistryFail, "boom");

        var r = await probe.ReadCurrentValueAsync(ReflexId, default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.RegistryFail);
    }

    [Fact]
    public async Task UnknownTweakId_FailsWithProbeFailed()
    {
        var (probe, _, _, _) = Build();

        var r = await probe.ReadCurrentValueAsync("does.not.exist", default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ProbeFailed);
    }

    [Fact]
    public async Task ScheduledTaskSurface_FailsAsNotSupportedInV1()
    {
        // Un catalogue qui associe un identifiant à la surface ScheduledTask (aucune feuille d'op en V1).
        var catalog = new StubCatalog(new ReconcilableDescriptor(
            "some.scheduled", ReconcileSurface.ScheduledTask));
        var probe = new SurfaceSettingProbe(catalog, new FakeRegistry(), new FakePowerPlan(), new FakeService());

        probe.CanProbe("some.scheduled").Should().BeFalse();

        var r = await probe.ReadCurrentValueAsync("some.scheduled", default);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("V1");
    }

    [Fact]
    public async Task RegistrySurfaceWithoutDescriptorLocation_FailsClean()
    {
        // Défensif : un descripteur de surface Registry sans sa sous-localisation Registry.
        var catalog = new StubCatalog(new ReconcilableDescriptor(
            "bad.reg", ReconcileSurface.Registry, Registry: null));
        var probe = new SurfaceSettingProbe(catalog, new FakeRegistry(), new FakePowerPlan(), new FakeService());

        probe.CanProbe("bad.reg").Should().BeFalse();

        var r = await probe.ReadCurrentValueAsync("bad.reg", default);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.ProbeFailed);
    }

    private sealed class StubCatalog : IReconcilableTweakCatalog
    {
        private readonly ReconcilableDescriptor _d;
        public StubCatalog(ReconcilableDescriptor d) => _d = d;
        public ReconcilableDescriptor? DescriptorFor(string tweakId)
            => string.Equals(tweakId, _d.TweakId, StringComparison.Ordinal) ? _d : null;
    }
}
