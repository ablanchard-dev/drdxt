using System.Collections.Concurrent;
using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// ReconcilerLifecycle (T37) — the clean-uninstall mechanic. Every effect is a fake
// (no real SCM / Task Scheduler / timer / elevation): we assert the loop is stopped,
// the lease disarmed and the host removed (NO orphan), that revert reaches the same
// bounded typed op with each item's original value, and that a partial revert failure
// still stops + removes. The security test uses the real mapper/catalog/op pipeline so
// the typed op the elevated client receives is the production one — not a mock.
// -----------------------------------------------------------------------
public sealed class ReconcilerLifecycleTests
{
    // The three V1-mappable reconcilable ids (the real closed catalog).
    private const string RegistryId = "latency.reflex.lowLatencyMode"; // Registry DWORD leaf
    private const string PowerPlanId = "fixit.sanePowerPlan";          // PowerPlan GUID leaf
    private static readonly string PowerOriginal = Guid.NewGuid().ToString("D");
    private static readonly string PowerDesired = Guid.NewGuid().ToString("D");

    // ----- behavioural fakes (in-memory, deterministic) -----

    private sealed class FakeHostStopSignal : IHostStopSignal
    {
        public int StopCount { get; private set; }
        public bool Fail { get; set; }
        public Task<Result> StopAsync(CancellationToken ct)
        {
            StopCount++;
            return Task.FromResult(Fail
                ? Result.Fail(ErrorCodes.OpNotAllowed, "stop failed (test)")
                : Result.Ok());
        }
    }

    private sealed class FakeLeaseDisarm : ILeaseDisarm
    {
        public int DisarmCount { get; private set; }
        public bool Fail { get; set; }
        public Task<Result> DisarmAsync(CancellationToken ct)
        {
            DisarmCount++;
            return Task.FromResult(Fail
                ? Result.Fail(ErrorCodes.OpNotAllowed, "disarm failed (test)")
                : Result.Ok());
        }
    }

    private sealed class FakeHostUninstaller : IHostUninstaller
    {
        public int RemoveCount { get; private set; }
        public bool Fail { get; set; }
        public Task<Result> RemoveAsync(CancellationToken ct)
        {
            RemoveCount++;
            return Task.FromResult(Fail
                ? Result.Fail(ErrorCodes.OpNotAllowed, "remove failed (test)")
                : Result.Ok());
        }
    }

    /// <summary>A fixed verified-manifest store (LoadAsync only — uninstall never commits).</summary>
    private sealed class FixedDesiredStateStore : IDesiredStateStore
    {
        private readonly Result<DesiredState> _load;
        public FixedDesiredStateStore(DesiredState state) => _load = Result<DesiredState>.Ok(state);
        public FixedDesiredStateStore(Result<DesiredState> load) => _load = load;
        public Task<Result> CommitAsync(DesiredState state)
            => Task.FromResult(Result.Fail(ErrorCodes.OpNotAllowed, "verify-only (test)"));
        public Task<Result<DesiredState>> LoadAsync() => Task.FromResult(_load);
    }

    /// <summary>An ISettingProbe over an in-memory dictionary; a tweakId can be flagged to
    /// FAIL its read (fail-clean path).</summary>
    private sealed class FakeSettingProbe : ISettingProbe
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

        public FakeSettingProbe Set(string tweakId, string value) { _values[tweakId] = value; return this; }
        public FakeSettingProbe FailRead(string tweakId) { _failing.Add(tweakId); return this; }
        public bool CanProbe(string tweakId) => true;

        public Task<Result<string>> ReadCurrentValueAsync(string tweakId, CancellationToken ct)
        {
            if (_failing.Contains(tweakId))
                return Task.FromResult(Result<string>.Fail(ReconcilerErrorCodes.ProbeFailed, "probe failed (test)"));
            return Task.FromResult(_values.TryGetValue(tweakId, out var v)
                ? Result<string>.Ok(v)
                : Result<string>.Fail(ReconcilerErrorCodes.ProbeFailed, "no value (test)"));
        }
    }

    /// <summary>Records every revert and writes the value into a FakeSettingProbe so the
    /// read-back confirms. Can refuse a chosen id, or write a WRONG value (confirm path).</summary>
    private sealed class RecordingReapplyClient : IElevatedReapplyClient
    {
        private readonly FakeSettingProbe _probe;
        private readonly Func<string, bool>? _refuse;
        private readonly Func<string, bool>? _writeWrong;
        public List<(string tweakId, string value)> Calls { get; } = new();

        public RecordingReapplyClient(
            FakeSettingProbe probe, Func<string, bool>? refuse = null, Func<string, bool>? writeWrong = null)
        {
            _probe = probe;
            _refuse = refuse;
            _writeWrong = writeWrong;
        }

        public Task<Result> ReapplyAsync(string tweakId, string valueJson, CancellationToken ct)
        {
            if (_refuse is not null && _refuse(tweakId))
                return Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refused (test)"));
            Calls.Add((tweakId, valueJson));
            var landed = _writeWrong is not null && _writeWrong(tweakId) ? valueJson + "_WRONG" : valueJson;
            _probe.Set(tweakId, landed);
            return Task.FromResult(Result.Ok());
        }
    }

    /// <summary>Captures every PrivilegedOp dispatched to the elevated frontier — the
    /// Real typed-op the production reapply mapper produced. Used by the security test.</summary>
    private sealed class CapturingElevatedClient : IElevatedClient
    {
        public List<PrivilegedOp> Ops { get; } = new();
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Ops.Add(op);
            return Task.FromResult(Result.Ok());
        }
    }

    private static DesiredState ManifestWith(params AppliedTweak[] tweaks)
        => new(tweaks);

    private static AppliedTweak Reg(string original, string desired)
        => new(RegistryId, desired, original, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static AppliedTweak Power(string original, string desired)
        => new(PowerPlanId, desired, original, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private static ReconcilerLifecycle Build(
        IHostStopSignal stop,
        ILeaseDisarm disarm,
        IDesiredStateStore manifest,
        IElevatedReapplyClient reapply,
        ISettingProbe probe,
        IHostUninstaller uninstaller,
        IDiagnosticsLog log)
        => new(stop, disarm, manifest, reapply, probe, uninstaller, log);

    // ------------------------------------------------------------------
    // (1) Stop + disarm + remove, NO orphan.
    // ------------------------------------------------------------------
    [Fact]
    public async Task StopAndDisarm_StopsLoop_DisarmsLease_RemovesHost_NoOrphan()
    {
        var stop = new FakeHostStopSignal();
        var disarm = new FakeLeaseDisarm();
        var uninstaller = new FakeHostUninstaller();
        var probe = new FakeSettingProbe();
        var reapply = new RecordingReapplyClient(probe);
        var manifest = new FixedDesiredStateStore(ManifestWith());
        var log = new RecordingDiagnostics();

        var sut = Build(stop, disarm, manifest, reapply, probe, uninstaller, log);

        var result = await sut.StopAndDisarmAsync(revertToOriginal: false, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        stop.StopCount.Should().Be(1, "the reconcile loop must be stopped");
        disarm.DisarmCount.Should().Be(1, "the dead-man / auto-revert lease must be disarmed");
        uninstaller.RemoveCount.Should().Be(1, "the host registration must be removed (no re-spawn)");
        reapply.Calls.Should().BeEmpty("revertToOriginal=false writes nothing");
    }

    // ------------------------------------------------------------------
    // (2) With revert: each item restored to its original value via the typed op.
    //     Asserts the elevated client received the typed op carrying the original
    //     value and an UNCHANGED (descriptor-only) target — via the real pipeline.
    // ------------------------------------------------------------------
    [Fact]
    public async Task StopAndDisarm_WithRevert_RestoresEachItemToOriginal_ViaTypedOp()
    {
        // Real mapper + real catalog + capturing elevated frontier = the production op.
        var capturing = new CapturingElevatedClient();
        var reapply = new ElevatedReapplyClient(
            new DefaultReapplyOpMapper(new DefaultReconcilableTweakCatalog()), capturing);

        // A probe that already reads the original values, so the read-back confirms once
        // the (captured) write "lands". DWORD original "0", desired "1".
        var probe = new FakeSettingProbe().Set(RegistryId, "0").Set(PowerPlanId, PowerOriginal);

        var stop = new FakeHostStopSignal();
        var disarm = new FakeLeaseDisarm();
        var uninstaller = new FakeHostUninstaller();
        var manifest = new FixedDesiredStateStore(ManifestWith(
            Reg(original: "0", desired: "1"),
            Power(original: PowerOriginal, desired: PowerDesired)));
        var log = new RecordingDiagnostics();

        var sut = Build(stop, disarm, manifest, reapply, probe, uninstaller, log);

        var result = await sut.StopAndDisarmAsync(revertToOriginal: true, CancellationToken.None);

        result.IsOk.Should().BeTrue();

        // The typed ops the elevated client received carry the original value, and the
        // target is the descriptor's (never steered by the value).
        capturing.Ops.Should().HaveCount(2);

        var reg = capturing.Ops.OfType<SetRegistryValueOp>().Should().ContainSingle().Subject;
        reg.Hive.Should().Be(RegistryHiveKind.LocalMachine);
        reg.Path.Should().Be(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak");
        reg.Name.Should().Be("LowLatencyMode");
        reg.Value.Should().Be("0", "the revert carries the ORIGINAL value, not the desired '1'");

        var power = capturing.Ops.OfType<SetActivePowerPlanOp>().Should().ContainSingle().Subject;
        power.PlanGuid.Should().Be(Guid.Parse(PowerOriginal),
            "the revert carries the ORIGINAL power-plan GUID, not the desired one");

        // And the lifecycle still stopped + disarmed + removed.
        stop.StopCount.Should().Be(1);
        disarm.DisarmCount.Should().Be(1);
        uninstaller.RemoveCount.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // (3) Without revert: no setting is written.
    // ------------------------------------------------------------------
    [Fact]
    public async Task StopAndDisarm_WithoutRevert_DoesNotWriteSettings()
    {
        var probe = new FakeSettingProbe().Set(RegistryId, "1");
        var reapply = new RecordingReapplyClient(probe);
        var stop = new FakeHostStopSignal();
        var disarm = new FakeLeaseDisarm();
        var uninstaller = new FakeHostUninstaller();
        var manifest = new FixedDesiredStateStore(ManifestWith(Reg(original: "0", desired: "1")));

        var sut = Build(stop, disarm, manifest, reapply, probe, uninstaller, new RecordingDiagnostics());

        var result = await sut.StopAndDisarmAsync(revertToOriginal: false, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        reapply.Calls.Should().BeEmpty("no item must be written when revertToOriginal=false");
        stop.StopCount.Should().Be(1);
        disarm.DisarmCount.Should().Be(1);
        uninstaller.RemoveCount.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // (4) One revert fails: still stops + disarms + removes; aggregates fail. No orphan.
    // ------------------------------------------------------------------
    [Fact]
    public async Task StopAndDisarm_OneRevertFails_StillStopsAndRemoves_AggregatesFail()
    {
        var probe = new FakeSettingProbe().Set(RegistryId, "0").Set(PowerPlanId, PowerOriginal);
        // Refuse the registry revert; the power-plan revert still succeeds.
        var reapply = new RecordingReapplyClient(probe, refuse: id => id == RegistryId);

        var stop = new FakeHostStopSignal();
        var disarm = new FakeLeaseDisarm();
        var uninstaller = new FakeHostUninstaller();
        var manifest = new FixedDesiredStateStore(ManifestWith(
            Reg(original: "0", desired: "1"),
            Power(original: PowerOriginal, desired: PowerDesired)));

        var sut = Build(stop, disarm, manifest, reapply, probe, uninstaller, new RecordingDiagnostics());

        var result = await sut.StopAndDisarmAsync(revertToOriginal: true, CancellationToken.None);

        // Fail-CLEAN: aggregated failure...
        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.UninstallRevertPartial);

        // ...but NO orphan: loop stopped, lease disarmed, host removed regardless.
        stop.StopCount.Should().Be(1);
        disarm.DisarmCount.Should().Be(1);
        uninstaller.RemoveCount.Should().Be(1);

        // The succeeding item was still reverted to its original value.
        reapply.Calls.Should().ContainSingle()
            .Which.Should().Be((PowerPlanId, PowerOriginal));
    }

    // ------------------------------------------------------------------
    // (4b) A read-back that does not confirm the original value is a failure, but the
    //      host is still stopped + removed (no orphan).
    // ------------------------------------------------------------------
    [Fact]
    public async Task StopAndDisarm_RevertNotConfirmed_AggregatesFail_StillRemoves()
    {
        var probe = new FakeSettingProbe().Set(RegistryId, "0");
        // The write "lands" a WRONG value, so the read-back never matches the original.
        var reapply = new RecordingReapplyClient(probe, writeWrong: id => id == RegistryId);

        var stop = new FakeHostStopSignal();
        var disarm = new FakeLeaseDisarm();
        var uninstaller = new FakeHostUninstaller();
        var manifest = new FixedDesiredStateStore(ManifestWith(Reg(original: "0", desired: "1")));

        var sut = Build(stop, disarm, manifest, reapply, probe, uninstaller, new RecordingDiagnostics());

        var result = await sut.StopAndDisarmAsync(revertToOriginal: true, CancellationToken.None);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.UninstallRevertPartial);
        uninstaller.RemoveCount.Should().Be(1, "a non-confirming revert must still remove the host");
    }

    // ------------------------------------------------------------------
    // (5) SECURITY (I3): a revert can only target tweakIds in the verified manifest, and
    //     the value→op mapping rules apply to the original value exactly as for desired.
    //     An off-manifest / non-allowlisted id produces NO op (fail before any IPC).
    // ------------------------------------------------------------------
    [Fact]
    public async Task StopAndDisarm_Revert_CannotBeSteeredToOffManifestTarget()
    {
        var capturing = new CapturingElevatedClient();
        var reapply = new ElevatedReapplyClient(
            new DefaultReapplyOpMapper(new DefaultReconcilableTweakCatalog()), capturing);

        // A manifest carrying an UNKNOWN / off-catalog tweakId. The mapper has no
        // descriptor for it → it fails before any IPC, so NO op is ever dispatched.
        var bogus = new AppliedTweak(
            "evil.unmapped.target", "desired", "original",
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var manifest = new FixedDesiredStateStore(ManifestWith(bogus));

        var probe = new FakeSettingProbe(); // never reached for the bogus id
        var stop = new FakeHostStopSignal();
        var disarm = new FakeLeaseDisarm();
        var uninstaller = new FakeHostUninstaller();

        var sut = Build(stop, disarm, manifest, reapply, probe, uninstaller, new RecordingDiagnostics());

        var result = await sut.StopAndDisarmAsync(revertToOriginal: true, CancellationToken.None);

        capturing.Ops.Should().BeEmpty(
            "an off-manifest / unmapped tweakId must NEVER produce a privileged op (I3)");
        result.IsOk.Should().BeFalse("the unmapped revert is a recorded failure");
        result.Error!.Value.Code.Should().Be(ReconcilerErrorCodes.UninstallRevertPartial);

        // No orphan even on the adversarial path.
        stop.StopCount.Should().Be(1);
        disarm.DisarmCount.Should().Be(1);
        uninstaller.RemoveCount.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // (5b) The value cannot steer the op's TARGET: two manifest items with the same
    //      allowlisted id but different values both map to the same descriptor target.
    // ------------------------------------------------------------------
    [Fact]
    public async Task StopAndDisarm_Revert_ValueNeverSteersTheTarget()
    {
        var capturing = new CapturingElevatedClient();
        var reapply = new ElevatedReapplyClient(
            new DefaultReapplyOpMapper(new DefaultReconcilableTweakCatalog()), capturing);

        // An original value that, if it leaked into the target, could point elsewhere —
        // it must only land in the bounded Value field; the target stays the descriptor's.
        var probe = new FakeSettingProbe().Set(RegistryId, "7");
        var manifest = new FixedDesiredStateStore(ManifestWith(Reg(original: "7", desired: "1")));

        var sut = Build(new FakeHostStopSignal(), new FakeLeaseDisarm(), manifest, reapply, probe,
            new FakeHostUninstaller(), new RecordingDiagnostics());

        var result = await sut.StopAndDisarmAsync(revertToOriginal: true, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        var reg = capturing.Ops.OfType<SetRegistryValueOp>().Should().ContainSingle().Subject;
        reg.Path.Should().Be(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak",
            "the target is descriptor-only — the original value never steers it");
        reg.Name.Should().Be("LowLatencyMode");
        reg.Value.Should().Be("7", "the original value lands ONLY in the bounded value field");
    }
}
