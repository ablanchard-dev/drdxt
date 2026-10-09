using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// DefaultReapplyOpMapper — the SECURITY BOUNDARY of the reconciler (the WinRing0
// lesson, I3). It resolves a reconcilable tweakId + desired serialized value to ONE
// of the existing sealed PrivilegedOp leaves with BOUNDED fields, always sourcing the
// op's TARGET (hive/path/name/serviceName) from the descriptor catalog — never from
// the caller-supplied desired value. An unknown tweakId, an unparseable / out-of-type
// value, or an unsupported surface → Fail before any op is produced.
//
// These tests prove: each surface's happy path (op fields == descriptor + canonical
// value), the parse rejections, the service-token → action mapping (incl. refusing
// install / notinstalled), and the hard guarantee that a HOSTILE desired value cannot
// move the op's target — only its bounded value field.
// -----------------------------------------------------------------------
public sealed class DefaultReapplyOpMapperTests
{
    private readonly DefaultReconcilableTweakCatalog _catalog = new();
    private readonly DefaultReapplyOpMapper _mapper;

    public DefaultReapplyOpMapperTests()
        => _mapper = new DefaultReapplyOpMapper(_catalog);

    // ---- Registry surface ----------------------------------------------------

    [Fact]
    public void Registry_Dword_MapsToSetRegistryValueOp_WithDescriptorTargetAndCanonicalValue()
    {
        var result = _mapper.Map("latency.reflex.lowLatencyMode", "2");

        result.IsOk.Should().BeTrue();
        var op = result.Value.Should().BeOfType<SetRegistryValueOp>().Subject;
        // Target comes entirely from the descriptor (not the desired value).
        op.Hive.Should().Be(RegistryHiveKind.LocalMachine);
        op.Path.Should().Be(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak");
        op.Name.Should().Be("LowLatencyMode");
        op.Kind.Should().Be(RegistryValueType.DWord);
        // Value is the canonical DWord form (byte-identical to what the probe reads).
        op.Value.Should().Be("2");
    }

    [Fact]
    public void Registry_Hags_MapsToHklmHwSchModeOp_FromDescriptor()
    {
        var result = _mapper.Map("os.hags", "2");

        result.IsOk.Should().BeTrue();
        var op = result.Value.Should().BeOfType<SetRegistryValueOp>().Subject;
        op.Hive.Should().Be(RegistryHiveKind.LocalMachine);
        op.Path.Should().Be(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        op.Name.Should().Be("HwSchMode");
        op.Kind.Should().Be(RegistryValueType.DWord);
        op.Value.Should().Be("2");
    }

    [Fact]
    public void HkcuNs1Tweak_HasNoDescriptor_SoTheMapperRefusesIt()
    {
        // os.gameDvrBackground is HKCU and intentionally not reconcilable by the LocalSystem
        // boot reconciler → no descriptor → the mapper fails before producing any op.
        _mapper.Map("os.gameDvrBackground", "0").IsOk.Should().BeFalse();
    }

    [Fact]
    public void Registry_Dword_ReSerializesThroughCanonicalizer_SoLeadingZerosAreNormalized()
    {
        // "007" parses as the uint 7 → the op carries the canonical "7", matching probe.
        var result = _mapper.Map("latency.reflex.lowLatencyMode", "007");

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeOfType<SetRegistryValueOp>()
            .Which.Value.Should().Be("7");
    }

    [Fact]
    public void Registry_Dword_RejectsNonNumericValue_NoOp()
    {
        var result = _mapper.Map("latency.reflex.lowLatencyMode", "not-a-number");

        result.IsOk.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error!.Value.Message.Should().Contain("DWord");
    }

    [Fact]
    public void Registry_Dword_RejectsNegativeAndOverflow_NoOp()
    {
        // -1 is not a valid uint; 4294967296 overflows uint.
        _mapper.Map("latency.reflex.lowLatencyMode", "-1").IsOk.Should().BeFalse();
        _mapper.Map("latency.reflex.lowLatencyMode", "4294967296").IsOk.Should().BeFalse();
    }

    // ---- PowerPlan surface ---------------------------------------------------

    [Fact]
    public void PowerPlan_ValidGuid_MapsToSetActivePowerPlanOp()
    {
        var guid = KnownPowerPlans.HighPerformance;
        var result = _mapper.Map("fixit.sanePowerPlan",
            ReconcilableValueCanonicalizer.PowerPlan(guid));

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeOfType<SetActivePowerPlanOp>()
            .Which.PlanGuid.Should().Be(guid);
    }

    [Fact]
    public void PowerPlan_InvalidGuid_RejectsNoOp()
    {
        var result = _mapper.Map("fixit.sanePowerPlan", "not-a-guid");

        result.IsOk.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error!.Value.Message.Should().Contain("GUID");
    }

    // ---- Service surface -----------------------------------------------------

    [Fact]
    public void Service_RunningToken_MapsToStartActionOnDescriptorService()
    {
        var result = _mapper.Map("fixit.x3dVCacheService",
            ReconcilableValueCanonicalizer.Service(ServiceRunState.Running));

        result.IsOk.Should().BeTrue();
        var op = result.Value.Should().BeOfType<RunServiceControlOp>().Subject;
        op.ServiceName.Should().Be("AMD3DVCacheSvc");
        op.Action.Should().Be(ServiceAction.Start);
    }

    [Fact]
    public void Service_StoppedToken_MapsToStopActionOnDescriptorService()
    {
        var result = _mapper.Map("fixit.x3dVCacheService",
            ReconcilableValueCanonicalizer.Service(ServiceRunState.Stopped));

        result.IsOk.Should().BeTrue();
        var op = result.Value.Should().BeOfType<RunServiceControlOp>().Subject;
        op.ServiceName.Should().Be("AMD3DVCacheSvc");
        op.Action.Should().Be(ServiceAction.Stop);
    }

    [Fact]
    public void Service_NotInstalledToken_Rejects_NeverInstalls()
    {
        // The reconciler does not install/uninstall as drift-repair (install is an
        // apply-time concern); ServiceAction.Install must never come from the mapper.
        var result = _mapper.Map("fixit.x3dVCacheService",
            ReconcilableValueCanonicalizer.Service(ServiceRunState.NotInstalled));

        result.IsOk.Should().BeFalse();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void Service_UnknownToken_Rejects()
        => _mapper.Map("fixit.x3dVCacheService", "garbage").IsOk.Should().BeFalse();

    // ---- Unknown id / unsupported surfaces -----------------------------------

    [Fact]
    public void UnknownTweakId_Rejects_BeforeAnythingElse()
    {
        var result = _mapper.Map("does.not.exist", "anything");

        result.IsOk.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.TweakNotAllowlisted);
    }

    [Fact]
    public void DrsBackedLatencyId_Rejects_NoOpLeafInV1()
    {
        // latency.renderQueueCap is policy-mapped to Registry but applied via NVAPI DRS;
        // it is absent from the catalog, so the mapper treats it as unknown.
        _mapper.Map("latency.renderQueueCap", "1").IsOk.Should().BeFalse();
        _mapper.Map("latency.vrrVsyncCap", "1").IsOk.Should().BeFalse();
    }

    // ---- Defense in depth: hostile value cannot move the target --------------

    [Fact]
    public void HostileDesiredValue_CannotChangeRegistryTarget_OnlyTheBoundedValue()
    {
        // A desired value that TRIES to smuggle a different hive/path/name (as JSON or
        // delimited text) must not influence the op's target. For a DWord leaf it simply
        // fails to parse → no op at all; the target can never be attacker-chosen.
        var hostile = "{\"Hive\":1,\"Path\":\"SYSTEM\\\\EVIL\",\"Name\":\"Pwned\",\"Value\":\"2\"}";

        var result = _mapper.Map("latency.reflex.lowLatencyMode", hostile);

        // Not a valid DWord → rejected outright (no op produced).
        result.IsOk.Should().BeFalse();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void EvenWithAParseableValue_TheTargetIsAlwaysTheDescriptors_NotTheInput()
    {
        // The desired value is a perfectly valid DWord ("2") — so an op IS produced — but
        // every target field still comes from the descriptor, never from the caller.
        var result = _mapper.Map("latency.reflex.lowLatencyMode", "2");

        var op = result.Value.Should().BeOfType<SetRegistryValueOp>().Subject;
        var descriptor = _catalog.DescriptorFor("latency.reflex.lowLatencyMode")!.Registry!;
        op.Hive.Should().Be(descriptor.Hive);
        op.Path.Should().Be(descriptor.Path);
        op.Name.Should().Be(descriptor.Name);
        op.Kind.Should().Be(descriptor.Kind);
    }
}
