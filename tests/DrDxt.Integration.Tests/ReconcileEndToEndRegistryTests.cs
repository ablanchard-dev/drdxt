using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Adapters;
using FluentAssertions;
using Microsoft.Win32;

namespace DrDxt.Integration.Tests;

// =======================================================================
// T35 — end-to-end reconcile against a real registry key (no elevation).
//
// The whole reconcile loop is wired with the real pieces — the only thing
// short-circuited is the elevated TRANSPORT (no IPC, no UAC):
//   * SurfaceSettingProbe (real) reads the real HKCU test leaf via its production
//     Win32 raw reader,
//   * DefaultReapplyOpMapper (real) resolves the tweakId → the existing typed
//     SetRegistryValueOp from a test catalog,
//   * ElevatedReapplyClient (real) dispatches that op through an in-process
//     IElevatedClient that runs the real R1 registry write leaf
//     (Win32RegistryActions.SetValue) directly — no by-hand registry write,
//   * JsonReconcilerStateStore (real) over a TEMP file (no ProgramData / admin).
//
// HKCU is user-writable, so this needs no elevation. The test key lives under an
// isolated, unique subkey and is DELETED in a finally so reruns stay clean.
//
// The production catalog/policy are a CLOSED 3-entry table — we do not add a test
// id to them; a test catalog + test policy carry the one "integ.test.dword" id for
// this test only (the same byte-exact canonicalization the production probe uses).
// =======================================================================
[Trait("Category", "Integration")]
[SupportedOSPlatform("windows")]
public sealed class ReconcileEndToEndRegistryTests
{
    private const string TweakId = "integ.test.dword";
    private const string KeyPath = @"Software\DrDxt\IntegrationTest";
    private const string ValueName = "Value";

    // The desired/original DWORDs, canonicalized to the exact JSON the probe reads
    // back (invariant decimal) so the reconciler's ordinal comparison holds.
    private static readonly string DesiredJson = ReconcilableValueCanonicalizer.Dword(7);
    private static readonly string OriginalJson = ReconcilableValueCanonicalizer.Dword(0);
    private static readonly DateTimeOffset AppliedAt = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RealHkcuDrift_IsReconciledBackToDesired_ThenSecondPassIsNoOp()
    {
        // Skip cleanly on a non-Windows runner (the registry/HKCU surface is Windows-only).
        if (!OperatingSystem.IsWindows())
            return;

        // A unique isolated leaf per run so concurrent/repeat runs never collide.
        var uniqueValueName = ValueName + "_" + Guid.NewGuid().ToString("N");
        var catalog = new TestCatalog(uniqueValueName);
        var statePath = Path.Combine(
            Path.GetTempPath(), "drdxt-integ-" + Guid.NewGuid().ToString("N") + ".json");

        // Real probe over the real HKCU leaf; real mapper over the same catalog; the
        // in-process elevated client runs the real R1 registry write leaf.
        var probe = new SurfaceSettingProbe(catalog);
        var mapper = new DefaultReapplyOpMapper(catalog);
        var elevated = new CountingInProcessElevatedClient(new Win32RegistryActions());
        var reapply = new ElevatedReapplyClient(mapper, elevated);
        var stateStore = new JsonReconcilerStateStore(statePath);
        var notifier = new RecordingNotifier();
        var diagnostics = new RecordingDiagnostics();
        var policy = new TestPolicy();
        var bootGuard = new BootLoopGuard(new HealthyBootStore());
        // A build probe reporting a build DIFFERENT from the item's LastSeenBuild, so a
        // drift back to the original value classifies as WindowsReset → re-apply.
        var buildProbe = new FixedBuildProbe("22635.5000");
        var driverProbe = new NoDriverProbe();

        var reconciler = new DesiredStateReconciler(
            probe, buildProbe, driverProbe, stateStore, reapply, notifier, policy,
            diagnostics, bootGuard, new SystemClock());

        // Seed the sidecar so the item is "known applied" at an OLDER build (the build
        // change is what makes a drift-to-original a WindowsReset, not a user choice).
        var seeded = ReconcilerState.Default with
        {
            Items = new[]
            {
                new ReconcilerItemState(TweakId, Paused: false, LastSeenBuild: "22631.3447",
                    LastSeenDriverVersion: string.Empty, LastReconciledAt: AppliedAt, LastResultCode: "OK"),
            },
        };
        (await stateStore.SaveAsync(seeded, default)).IsOk.Should().BeTrue();

        var desired = new DesiredState(new[]
        {
            new AppliedTweak(TweakId, DesiredJson, OriginalJson, AppliedAt),
        });

        try
        {
            // (1) Apply desired: write the desired DWORD to the real HKCU leaf via the
            //     Real write leaf, and confirm the probe reads it back byte-exact.
            WriteDword(uniqueValueName, 7);
            (await probe.ReadCurrentValueAsync(TweakId, default)).Value
                .Should().Be(DesiredJson, "the seeded value is the canonical desired");

            // (2) Mutate the key "like Windows": a feature update reset it to original (0).
            WriteDword(uniqueValueName, 0);

            // (3) Reconcile: the drift (0 != 7) is classified WindowsReset and re-applied.
            var first = await reconciler.ReconcileWithReportAsync(desired, default);

            first.result.IsOk.Should().BeTrue();
            ReadDword(uniqueValueName).Should().Be(7, "the reconciler RESTORED the real key to desired");
            first.report.Items.Should().ContainSingle()
                .Which.Outcome.Should().Be(ItemOutcome.Reapplied);
            elevated.ExecuteCount.Should().Be(1, "exactly one real registry write was issued");
            diagnostics.Changes.Should().ContainSingle(c =>
                c.TweakId == TweakId && c.To == DesiredJson && c.Actor == "DrDXT");

            // (4) SECOND pass on the now-correct key → strict NO-OP: no new write at all.
            var second = await reconciler.ReconcileWithReportAsync(desired, default);

            second.result.IsOk.Should().BeTrue();
            second.report.Items.Should().ContainSingle()
                .Which.Outcome.Should().Be(ItemOutcome.NoOp);
            elevated.ExecuteCount.Should().Be(1,
                "idempotence on a REAL key: the second pass issued ZERO new writes");
            ReadDword(uniqueValueName).Should().Be(7, "the key is unchanged by the no-op pass");
        }
        finally
        {
            // Negative/cleanup: the isolated test value and (if now empty) key are removed.
            DeleteTestArtifacts();
            if (File.Exists(statePath))
                File.Delete(statePath);
        }

        // Cleanup assertion: nothing of ours is left behind under HKCU.
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        key?.GetValue(uniqueValueName).Should().BeNull("the test value was deleted on cleanup");
    }

    // ---- real-registry helpers (HKCU is user-writable; no elevation needed) ----

    private static void WriteDword(string valueName, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    private static int ReadDword(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        key.Should().NotBeNull();
        return (int)key!.GetValue(valueName)!;
    }

    private static void DeleteTestArtifacts()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null)
            return;
        foreach (var name in key.GetValueNames())
            key.DeleteValue(name, throwOnMissingValue: false);
        // Remove the now-empty test subkey tree we created (best-effort, isolated path).
        if (key.GetValueNames().Length == 0 && key.GetSubKeyNames().Length == 0)
            Registry.CurrentUser.DeleteSubKey(KeyPath, throwOnMissingSubKey: false);

        // 24/09/2026 : le parent « Software\DrDxt », créé en passant par CreateSubKey, restait vide
        // dans le registre de la machine de test. Le produit n'utilise pas cette clé : vide, elle part.
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\DrDxt");
        if (parent is { ValueCount: 0, SubKeyCount: 0 })
        {
            parent.Dispose();
            Registry.CurrentUser.DeleteSubKey(@"Software\DrDxt", throwOnMissingSubKey: false);
        }
    }

    // ---- test-only catalog + policy carrying the single integ id ----

    private sealed class TestCatalog : IReconcilableTweakCatalog
    {
        private readonly ReconcilableDescriptor _descriptor;

        public TestCatalog(string valueName)
            => _descriptor = new ReconcilableDescriptor(
                TweakId,
                ReconcileSurface.Registry,
                Registry: new RegistryDescriptor(
                    RegistryHiveKind.CurrentUser, KeyPath, valueName, RegistryValueType.DWord));

        public ReconcilableDescriptor? DescriptorFor(string tweakId)
            => string.Equals(tweakId, TweakId, StringComparison.Ordinal) ? _descriptor : null;
    }

    private sealed class TestPolicy : IReconcilabilityPolicy
    {
        public ReconcileSurface? SurfaceFor(string tweakId)
            => string.Equals(tweakId, TweakId, StringComparison.Ordinal)
                ? ReconcileSurface.Registry
                : null;

        public bool IsProtected(string tweakId) => false;
    }

    // ---- the in-process elevated client: real R1 write leaf, no IPC, counting ----

    private sealed class CountingInProcessElevatedClient : IElevatedClient
    {
        private readonly IRegistryActions _registry;

        public CountingInProcessElevatedClient(IRegistryActions registry) => _registry = registry;

        public int ExecuteCount { get; private set; }

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            ExecuteCount++;
            // Exercise the real op→registry path (the same leaf R1 ships); never the IPC.
            return Task.FromResult(op switch
            {
                SetRegistryValueOp r => _registry.SetValue(r.Hive, r.Path, r.Name, r.Kind, r.Value),
                _ => Result.Fail(ErrorCodes.HelperOpFailed, $"Unexpected op {op.GetType().Name} in T35."),
            });
        }
    }

    // ---- recording / fixed seams ----

    private sealed record DiagChange(string TweakId, string From, string To, string Actor);

    private sealed class RecordingDiagnostics : IDiagnosticsLog
    {
        public List<DiagChange> Changes { get; } = new();

        public void Change(string tweakId, string from, string to, string actor)
            => Changes.Add(new DiagChange(tweakId, from, to, actor));

        public Task<Result<string>> ExportAsync()
            => Task.FromResult(Result<string>.Ok(string.Empty));
    }

    private sealed class RecordingNotifier : IReconcilerNotifier
    {
        public int Count { get; private set; }

        public Task NotifyUserIntentionalDriftAsync(AppliedTweak tweak, string currentValueJson, CancellationToken ct)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedBuildProbe : IOsBuildProbe
    {
        private readonly string _build;
        public FixedBuildProbe(string build) => _build = build;
        public Result<string> ReadCurrentBuild() => Result<string>.Ok(_build);
    }

    private sealed class NoDriverProbe : IGpuDriverProbe
    {
        public Result<string?> ReadDriverVersion(string vendor) => Result<string?>.Ok(null);
    }

    private sealed class HealthyBootStore : IBootOutcomeStore
    {
        public Task<Result<int>> GetUncleanStreakAsync(CancellationToken ct)
            => Task.FromResult(Result<int>.Ok(0));
        public Task<Result> MarkBootStartedAsync(CancellationToken ct) => Task.FromResult(Result.Ok());
        public Task<Result> MarkSessionStableAsync(CancellationToken ct) => Task.FromResult(Result.Ok());
    }
}
