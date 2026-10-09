using DrDxt.Application.Engine;
using DrDxt.Application.Safety;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// =======================================================================
// M6 — the isolation gate: "a GPU failure NEVER rolls back the safe batch".
//
// This is the WHOLE reason GPU tuning is a dedicated, SEPARATE engine batch (the
// GpuTuningApplyStep runs `consumption.GpuPlan` as its own engine.ApplyAsync). With the
// Real TweakEngine + real SafetyManager + real catalog of real tweaks (only the SDK boundary
// faked by FakeSystemState + FakeElevatedClient + read-seams that report from that state) we
// model exactly that flow: apply the SAFE batch (kept), then run the GPU plan as a SECOND,
// independent engine batch, and assert:
//   1. when the GPU batch FAILS mid-apply (a refused write), the engine LIFO-reverts only the
//      GPU batch — the previously-applied safe batch is byte-identical to its kept state;
//   2. when the GPU batch is kept-then-regresses, the engine auto-reverts only the GPU change
//      (the R2 measured-regression revert net) — again the safe batch is untouched.
//
// (The step's "run GpuPlan as its own batch" + re-clamp + no-op are unit-tested in
//  DrDxt.App.Tests/GpuTuningApplyStepTests; here we prove the engine-level isolation the step
//  relies on, end-to-end against a real machine model.)
// =======================================================================
public sealed class GpuTuningIsolationTests
{
    private const int Pid = 4242;

    private static readonly BenchmarkResult Baseline =
        new(200, 150, 120, new[] { 5.0, 5.0 }, 15, MarginOfError: 1, Runs: 1);

    // "after" == baseline → no regression, no gain → the batch is KEPT.
    private sealed class NeutralBenchmark : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct) =>
            Task.FromResult(Result<BenchmarkResult>.Ok(
                new BenchmarkResult(200, 150, 120, new[] { 5.0, 5.0 }, 15, MarginOfError: 1, Runs: 1)));
    }

    // "after" much worse than baseline → a MEASURED regression → the engine auto-reverts.
    private sealed class RegressionBenchmark : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct) =>
            Task.FromResult(Result<BenchmarkResult>.Ok(
                new BenchmarkResult(120, 80, 60, new[] { 8.0, 8.1 }, 25, MarginOfError: 1, Runs: 1)));
    }

    private sealed class EnabledRestore : IRestorePointService
    {
        public bool IsEnabled() => true;
        public bool TryEnable() => true;
        public DateTime? DernierPointDepuis(DateTime depuisUtc) => DateTime.UtcNow;
    }

    private sealed class GrantingConsent : IConsentPrompt
    {
        public bool AskEnableRestore() => true;
    }

    private sealed class NoOpDiagnostics : IDiagnosticsLog
    {
        public void Change(string tweakId, string from, string to, string actor) { }
        public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(string.Empty));
    }

    // A desktop NVIDIA baseline: stock GPU offsets/power, Balanced plan + a captured reflex
    // registry seed, so the SAFE batch (power-plan + reflex) applies + is capturable, and the
    // GPU batch (power-limit + undervolt) has stock originals to revert to.
    private static FakeSystemState NewBaseline()
    {
        var s = new FakeSystemState
        {
            NvApiAvailable = true,
            ActivePowerPlan = KnownPowerPlans.Balanced,
            NvLowLatencyMode = 0,
            GpuCoreOffsetMhz = 0,
            GpuMemoryOffsetMhz = 0,
            GpuPowerLimitPct = 100,
        };
        s.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode", 0);
        return s;
    }

    // The SAFE (non-GPU) plan and the SEPARATE GPU plan, mirroring what ScanProfileConsumer
    // produces (Plan vs GpuPlan).
    private static readonly TweakPlan SafePlan = new(new[] { "fixit.power_plan", "latency.reflex" });
    private static readonly TweakPlan GpuPlan = new(new[] { "gpu.power_limit", "gpu.undervolt" });

    private static TweakEngine BuildEngine(
        FakeSystemState state, IBenchmarkRunner bench, Func<PrivilegedOp, bool>? refuse, out FakeElevatedClient client)
    {
        client = new FakeElevatedClient(state, refuse);
        var safety = new SafetyManager(new EnabledRestore(), new GrantingConsent(), client);
        var diag = new NoOpDiagnostics();

        var nv = new FakeNvApi(state);
        var reg = new FakeRegistryReader(state);
        var power = new FakePowerPlanReader(state);
        var nvTuning = new FakeNvApiGpuTuning(state);
        var adlxTuning = new FakeAdlxGpuTuning(state);
        // The conservative in-bounds GPU targets (same as the App seeds).
        var gpuTargets = new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, 90));

        var tweaks = new ITweak[]
        {
            new SanePowerPlanTweak(power, client),
            new NvidiaUltraLowLatencyTweak(nv, reg, client),
            new GpuPowerLimitTweak(nvTuning, adlxTuning, client, gpuTargets),
            new GpuUndervoltTweak(nvTuning, adlxTuning, new StableStabilityTester(), client, gpuTargets),
        };
        var catalog = new TweakCatalog(tweaks);
        return new TweakEngine(safety, bench, diag, catalog);
    }

    // Applies the safe batch and asserts it was KEPT (not auto-reverted); returns its snapshot.
    private static async Task<string> ApplyKeptSafeBatchAsync(FakeSystemState state)
    {
        var engine = BuildEngine(state, new NeutralBenchmark(), refuse: null, out _);
        var safe = await engine.ApplyAsync(SafePlan, Baseline, Pid, default);

        safe.IsOk.Should().BeTrue();
        safe.Value!.Outcomes.Should().OnlyContain(o => o.Applied && !o.AutoReverted);
        // The safe batch really changed the machine (so the gate below isn't vacuous).
        state.ActivePowerPlan.Should().Be(KnownPowerPlans.HighPerformance);
        return state.Snapshot();
    }

    // =================== GATE 1: GPU apply failure leaves the safe batch intact ===================

    [Fact]
    public async Task Gpu_batch_apply_failure_does_not_roll_back_the_previously_applied_safe_batch()
    {
        var state = NewBaseline();
        var before = state.Snapshot();

        var afterSafe = await ApplyKeptSafeBatchAsync(state);
        afterSafe.Should().NotBe(before); // sanity: the safe batch is applied + kept.

        // The GPU batch runs as a SEPARATE engine call. Refuse the undervolt clock-offset
        // write so the GPU batch fails after the power-limit already wrote → the engine
        // LIFO-reverts the GPU batch (power-limit back to stock).
        static bool RefuseUndervoltWrite(PrivilegedOp op) =>
            op is ApplyNvApiClockOffsetOp { CoreOffsetMhz: -100 };

        var gpuEngine = BuildEngine(state, new NeutralBenchmark(), RefuseUndervoltWrite, out var gpuClient);
        var gpu = await gpuEngine.ApplyAsync(GpuPlan, Baseline, Pid, default);

        gpu.IsOk.Should().BeFalse();
        gpu.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
        // The GPU batch DID write (power-limit) before being rolled back → gate isn't vacuous.
        gpuClient.Writes.Should().NotBeEmpty();

        // THE GATE: the safe batch is byte-identical to its kept state — never rolled back.
        state.Snapshot().Should().Be(afterSafe);
        state.ActivePowerPlan.Should().Be(KnownPowerPlans.HighPerformance); // safe fix survives
        // And the GPU change itself was fully reverted (only the GPU change).
        state.GpuPowerLimitPct.Should().Be(100);
        state.GpuCoreOffsetMhz.Should().Be(0);
    }

    // =================== GATE 2: GPU measured-regression auto-reverts only the GPU change ===================

    [Fact]
    public async Task Gpu_batch_measured_regression_auto_reverts_only_the_gpu_change()
    {
        var state = NewBaseline();
        var afterSafe = await ApplyKeptSafeBatchAsync(state);

        // The GPU batch applies fully (stable), then a regression bench makes the engine
        // auto-revert the WHOLE GPU batch — the R2 measured-regression revert net.
        var gpuEngine = BuildEngine(state, new RegressionBenchmark(), refuse: null, out _);
        var gpu = await gpuEngine.ApplyAsync(GpuPlan, Baseline, Pid, default);

        gpu.IsOk.Should().BeTrue();
        gpu.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted");
        gpu.Value.Outcomes.Should().OnlyContain(o => o.AutoReverted);

        // THE GATE: the safe batch is byte-identical to its kept state; only the GPU reverted.
        state.Snapshot().Should().Be(afterSafe);
        state.ActivePowerPlan.Should().Be(KnownPowerPlans.HighPerformance);
        state.GpuPowerLimitPct.Should().Be(100);
        state.GpuCoreOffsetMhz.Should().Be(0);
    }
}
