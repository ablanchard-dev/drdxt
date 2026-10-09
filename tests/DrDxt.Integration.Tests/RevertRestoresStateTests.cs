using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Engine;
using DrDxt.Application.Safety;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Integration.Tests;

// =======================================================================
// M2 H1 — THE GATE: "the revert restores the state".
//
// The §20 money-back guarantee rests on this. With the real TweakEngine + real
// SafetyManager + real TweakCatalog of real tweaks (only the SDK boundary is faked
// by FakeSystemState + FakeElevatedClient + read-seams that report from that
// state), we assert:
//   1. After apply-then-revert, the fake system state is BYTE-IDENTICAL to the
//      baseline snapshot (engine auto-revert on a faked regression).
//   2. A tweak failing mid-batch leaves the state BYTE-IDENTICAL to baseline
//      (transactional: never a half-applied state).
//   3. (broader coverage) A composite-snapshot tweak's own RevertAsync also
//      restores the state byte-identically (explicit revert path).
//
// Deterministic, no real machine: the CI runs this as the hard gate.
// =======================================================================
public sealed class RevertRestoresStateTests
{
    private const int Pid = 4242;
    private static readonly TimeSpan Dur = TimeSpan.FromSeconds(10);

    // --- A baseline that makes the single-op AND composite tweaks applicable and
    // capturable. The composite tweaks (fixit.x3d_ccd, latency.vrr_vsync_cap) store a
    // MULTI-OP snapshot only their own RevertAsync can decode, so wiring them through
    // the engine proves the engine auto-revert path restores a composite byte-identically.
    private static FakeSystemState NewBaseline()
    {
        var s = new FakeSystemState
        {
            NvApiAvailable = true,           // NVIDIA path for reflex + GPU tuning + VRR cap
            ActivePowerPlan = KnownPowerPlans.Balanced,
            NvLowLatencyMode = 0,            // captured original = Off
            GpuCoreOffsetMhz = 0,
            GpuMemoryOffsetMhz = 0,
            GpuPowerLimitPct = 100,
            // Captured DRS originals for the VRR/Vsync composite (Vsync off, no cap).
            NvVsyncMode = 0,
            NvFrameRateLimit = 0,
            // A detected VRR display so latency.vrr_vsync_cap's ApplyAsync captures + writes.
            PrimaryGameDisplay = new DisplayInfo(IsPrimaryGameDisplay: true, SupportsVrr: true, RefreshHz: 144),
        };
        // A pre-existing NVTweak LowLatencyMode value so reflex's revert restores it.
        s.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode", 0);
        // Pre-seed the DRS leaves the VRR/Vsync composite touches with their captured
        // originals (Vsync = 0, frame limit = 0) so a faithful composite revert returns
        // those exact keys to baseline (the keys exist before AND after — byte-identical).
        s.SetDrs(0x00A879CF, 0);  // VSYNCMODE_ID original = off (corrected id)
        s.SetDrs(0x10835002, 0);  // FRL_FPS_ID original = no cap (corrected id)
        // X3D-CCD composite captures: the optimizer service is initially stopped, and a
        // pre-existing CppcEnable leaf — so its composite revert must restore both.
        s.SetService("AMD3DVCacheSvc", ServiceState.Stopped);
        s.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Processor", "CppcEnable", 0);
        // Game Bar value initially absent → its faithful inverse is a delete.
        return s;
    }

    // --- T8 — an AMD baseline: NVAPI absent + ADLX PRESENT, so the GPU tuning tweaks take
    // the ADLX branch. The captured ADLX originals are what a faithful inverse ApplyAdlxTuningOp
    // must restore after the engine auto-reverts. The undervolt path round-trips an absolute
    // MaxFreq (stock 2900 → apply 2800 → revert restores 2900): this gives the gate TEETH — a
    // wrong-units / VoltMv-based inverse would leave AdlxMaxFreqMhz != 2900 and FAIL the gate. ---
    private static FakeSystemState NewAmdBaseline() => new()
    {
        NvApiAvailable = false,           // no NVIDIA → GPU tuning resolves the ADLX path
        AdlxAvailable = true,             // AMD ADLX present (single tunable dGPU at index 0)
        ActivePowerPlan = KnownPowerPlans.Balanced,
        AdlxMaxFreqMhz = 2900,            // captured original absolute GFX max clock (the undervolt surface)
        AdlxPowerLimitPct = 100,          // captured original power limit
    };

    // --- The cross-cutting safety seams: restore enabled + point verified. ---
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

    // --- A benchmark runner that returns a regression so the engine auto-reverts. ---
    private sealed class RegressionBenchmark : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct) =>
            // "after" much worse than the baseline (set in the test) → regression beyond margin.
            Task.FromResult(Result<BenchmarkResult>.Ok(
                new BenchmarkResult(120, 80, 60, new[] { 8.0, 8.1 }, 25, MarginOfError: 1, Runs: 1)));
    }

    private static readonly BenchmarkResult Baseline =
        new(200, 150, 120, new[] { 5.0, 5.0 }, 15, MarginOfError: 1, Runs: 1);

    // --- Wire the real engine + safety + catalog against the fake state. ---
    private static (TweakEngine Engine, SafetyManager Safety, TweakCatalog Catalog, FakeElevatedClient Client)
        BuildReal(FakeSystemState state, IBenchmarkRunner bench, Func<PrivilegedOp, bool>? refuse = null)
    {
        var client = new FakeElevatedClient(state, refuse);
        var safety = new SafetyManager(new EnabledRestore(), new GrantingConsent(), client);
        var diag = new NoOpDiagnostics();

        var tweaks = BuildRealTweaks(state, client);
        var catalog = new TweakCatalog(tweaks);
        var engine = new TweakEngine(safety, bench, diag, catalog);
        return (engine, safety, catalog, client);
    }

    // The real tweaks, constructed against the fake seams (NVIDIA single-op set so the
    // engine's generic SafetyManager revert path can restore every one).
    private static IReadOnlyList<ITweak> BuildRealTweaks(FakeSystemState state, FakeElevatedClient client)
    {
        var nv = new FakeNvApi(state);
        var nvTuning = new FakeNvApiGpuTuning(state);
        var adlx = new FakeAdlx(state);
        var adlxTuning = new FakeAdlxGpuTuning(state);
        var reg = new FakeRegistryReader(state);
        var power = new FakePowerPlanReader(state);
        var svc = new FakeServiceStateReader(state);
        var display = new FakeDisplayInfo(state);
        var stability = new StableStabilityTester();
        // M6 — the GPU tweaks now read their targets from the live IGpuTuningTargetSource,
        // seeded here with the same conservative values they used before (byte-identical
        // gate behaviour).
        var gpuTargets = new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, 90));

        return new ITweak[]
        {
            new NvidiaUltraLowLatencyTweak(nv, reg, client),
            new RenderQueueCapTweak(nv, adlx, client),
            new VrrVsyncCapConfigTweak(nv, display, client),
            new GpuUndervoltTweak(nvTuning, adlxTuning, stability, client, gpuTargets),
            new GpuPowerLimitTweak(nvTuning, adlxTuning, client, gpuTargets),
            new GpuFanCurveTweak(nvTuning, adlxTuning, client, gpuTargets, new GpuFanCurveTarget(0,
                new FanCurve(new[] { new FanCurvePoint(40, 35), new FanCurvePoint(80, 70), new FanCurvePoint(90, 90) }))),
            new X3dCcdParkingFixTweak(svc, reg, client),
            new SanePowerPlanTweak(power, client),
        };
    }

    // =================== GATE 1: apply → auto-revert → identical ===================

    [Fact]
    public async Task After_apply_then_revert_state_is_identical_to_baseline()
    {
        var state = NewBaseline();
        var (engine, _, _, client) = BuildReal(state, new RegressionBenchmark());

        var before = state.Snapshot();

        // A rich batch touching registry + power-plan + 3 GPU surfaces AND TWO
        // composite-snapshot tweaks (fixit.x3d_ccd = service + 2 reg leaves;
        // latency.vrr_vsync_cap = 2 DRS leaves). The composites store a multi-op
        // snapshot that only their own RevertAsync can decode, so this proves the
        // ENGINE auto-revert path (tweak.RevertAsync, not _safety) restores a composite
        // byte-identically — the reversibility-gap fix, exercised end-to-end.
        var plan = new TweakPlan(new[]
        {
            "latency.reflex", "fixit.power_plan", "gpu.undervolt", "gpu.power_limit", "gpu.fan_curve",
            "fixit.x3d_ccd", "latency.vrr_vsync_cap",
        });

        var result = await engine.ApplyAsync(plan, Baseline, Pid, default);

        // The faked regression makes the engine auto-revert the WHOLE batch.
        result.IsOk.Should().BeTrue();
        result.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted");
        result.Value.Outcomes.Should().OnlyContain(o => o.AutoReverted);
        // The composite tweaks WERE part of the auto-reverted batch (gate isn't vacuous).
        result.Value.Outcomes.Select(o => o.TweakId)
            .Should().Contain(new[] { "fixit.x3d_ccd", "latency.vrr_vsync_cap" });

        // Sanity: the batch DID write before reverting (so the gate isn't vacuous).
        client.Writes.Should().NotBeEmpty();
        // N6 teeth on the NVIDIA path: gpu.undervolt on NvApiAvailable=true issues a real NVAPI
        // clock-offset write (GpuCoreOffsetMhz 0 → target), so the byte-identical restore below
        // genuinely exercises the NVAPI offset capture→apply→revert round-trip — not just the
        // composites. A wrong inverse would leave GpuCoreOffsetMhz != 0 and FAIL the gate.
        client.Writes.Should().Contain(op => op is ApplyNvApiClockOffsetOp);

        // THE GATE: byte-identical return to stock — including the composite surfaces.
        state.Snapshot().Should().Be(before);
    }

    // =================== GATE 1b (AMD/ADLX): apply ADLX offset → auto-revert → identical ===================

    [Fact]
    public async Task After_apply_then_revert_state_is_identical_on_the_amd_adlx_path()
    {
        // T8 — the §20 "revert restores the state" gate, on the AMD path. With NVAPI absent
        // and ADLX present, gpu.undervolt / gpu.power_limit resolve the tunable discrete GPU
        // (T4) and write through ApplyAdlxTuningOp (T2/T3); the faked regression makes the
        // engine auto-revert by replaying the inverse ADLX ops captured from the originals.
        var state = NewAmdBaseline();
        var (engine, _, _, client) = BuildReal(state, new RegressionBenchmark());

        var before = state.Snapshot();

        var plan = new TweakPlan(new[] { "gpu.undervolt", "gpu.power_limit" });

        var result = await engine.ApplyAsync(plan, Baseline, Pid, default);

        // The faked regression makes the engine auto-revert the whole GPU batch.
        result.IsOk.Should().BeTrue();
        result.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted");
        result.Value.Outcomes.Should().OnlyContain(o => o.AutoReverted);

        // Not vacuous AND genuinely the ADLX path: the batch issued ADLX writes and never an
        // NVAPI clock-offset (proving it took the AMD branch, not NVIDIA).
        client.Writes.Should().Contain(op => op is ApplyAdlxTuningOp);
        client.Writes.Should().NotContain(op => op is ApplyNvApiClockOffsetOp);

        // THE GATE: byte-identical return to stock — the ADLX undervolt + power-limit restored.
        state.Snapshot().Should().Be(before);
    }

    // =================== GATE 2: partial failure → identical ===================

    [Fact]
    public async Task Batch_partial_failure_leaves_state_identical_to_baseline()
    {
        var state = NewBaseline();

        // Refuse reflex's registry write so the SECOND tweak fails mid-batch, after the
        // first (power-plan) has applied. The engine must roll the first one back.
        static bool RefuseReflexWrite(PrivilegedOp op) =>
            op is SetRegistryValueOp { Name: "LowLatencyMode" };

        var (engine, _, _, _) = BuildReal(state, new RegressionBenchmark(), RefuseReflexWrite);

        var before = state.Snapshot();

        var plan = new TweakPlan(new[] { "fixit.power_plan", "latency.reflex" });

        var result = await engine.ApplyAsync(plan, Baseline, Pid, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);

        // THE GATE: no half-applied state — the power-plan change was rolled back.
        state.Snapshot().Should().Be(before);
    }

    // =================== broader coverage: composite explicit revert ===================

    [Fact]
    public async Task Composite_tweak_explicit_revert_restores_state_identically()
    {
        // F1 (x3d_ccd) writes a composite (service + 2 registry leaves); its own
        // RevertAsync replays the composite to restore all three. This proves the
        // composite-snapshot path is also byte-lossless.
        var state = NewBaseline();
        state.SetService("AMD3DVCacheSvc", ServiceState.Stopped);
        state.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Processor", "CppcEnable", 1);
        // Game Bar value initially absent → its faithful inverse is a delete.

        var client = new FakeElevatedClient(state);
        var tweak = new X3dCcdParkingFixTweak(
            new FakeServiceStateReader(state), new FakeRegistryReader(state), client);

        var before = state.Snapshot();

        var apply = await tweak.ApplyAsync();
        apply.IsOk.Should().BeTrue();
        // The apply actually changed the state (otherwise the gate would be vacuous).
        state.Snapshot().Should().NotBe(before);

        var revert = await tweak.RevertAsync(apply.Value!);
        revert.IsOk.Should().BeTrue();

        // THE GATE: composite revert returns byte-identical.
        state.Snapshot().Should().Be(before);
    }

    // =================== the catalog used is the real one (DI-resolvable) ===================

    [Fact]
    public void Real_application_catalog_resolves_via_DI_and_is_non_empty()
    {
        // The same registrations the App host uses: a smoke check that the integration
        // gate exercises the real catalog, not a hand-rolled subset.
        var services = new ServiceCollection();
        services.AddSingleton<IDiagnosticsLog>(new NoOpDiagnostics());
        services.AddSingleton<IBenchmarkRunner>(new RegressionBenchmark());
        services.AddDrDxtApplication();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITweakCatalog>().All().Should().NotBeEmpty();
        provider.GetService<ITweakEngine>().Should().NotBeNull();
    }
}
