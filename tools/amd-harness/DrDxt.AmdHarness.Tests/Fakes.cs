using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.AmdHarness.Tests;

// -----------------------------------------------------------------------
// Recording fakes for HarnessRunner orchestration tests.
//
// CRITICAL DESIGN NOTE — avoiding false positives (we hit exactly this trap
// in Task 1): these fakes do NOT pre-force flags that make a dead code path
// "pass". They record REAL calls made by the runner and return data the runner
// acts on. The test then asserts the runner's ACTUAL decisions (which plan was
// passed, whether revert was called, etc.), not a forced sentinel.
//
// Each fake that participates in order tracking takes a shared `List<string>`
// call log and appends a tag when called — so a single assertion over the log
// proves the full execution sequence without NSubstitute magic.
// -----------------------------------------------------------------------

// ── Call-log tags (one per observable operation) ─────────────────────────────
internal static class LogTag
{
    public const string Detect      = "detect";
    public const string ReadState   = "read-state";
    public const string Scan        = "scan";
    public const string EngineApply = "engine-apply";
    public const string AdlxProbe   = "adlx-probe";
    // Task 4: writer tags — logged by FakeAdlxGpuTuningWriter in call order
    public const string MicroWrite  = "adlx-micro-write";
    public const string MicroRevert = "adlx-micro-revert";
    public const string PerfWrite   = "adlx-perf-write";
    public const string PerfRevert  = "adlx-perf-revert";
    // Step 5c: voltage-probe write/revert (freq offset 0, volt offset != 0)
    public const string VoltWrite   = "adlx-volt-write";
    public const string VoltRevert  = "adlx-volt-revert";
    public const string Bench       = "bench";
    public const string Revert      = "revert";
    public const string Cleanup     = "cleanup";
}

// ── IHardwareDetector ────────────────────────────────────────────────────────
internal sealed class FakeHardwareDetector : IHardwareDetector
{
    private readonly List<string> _log;
    private readonly Result<HardwareInventory> _result;

    public FakeHardwareDetector(List<string> log, Result<HardwareInventory> result)
    {
        _log    = log;
        _result = result;
    }

    public Task<Result<HardwareInventory>> DetectAsync()
    {
        _log.Add(LogTag.Detect);
        return Task.FromResult(_result);
    }
}

// ── ISettingsStateReader ─────────────────────────────────────────────────────
internal sealed class FakeSettingsStateReader : ISettingsStateReader
{
    private readonly List<string> _log;
    private readonly Result<SettingsState> _result;

    public FakeSettingsStateReader(List<string> log, Result<SettingsState> result)
    {
        _log    = log;
        _result = result;
    }

    public Task<Result<SettingsState>> ReadAsync(HardwareInventory hw)
    {
        _log.Add(LogTag.ReadState);
        return Task.FromResult(_result);
    }
}

// ── IScanScorer ──────────────────────────────────────────────────────────────
internal sealed class FakeScanScorer : IScanScorer
{
    private readonly List<string> _log;
    private readonly Result<ScanResult> _result;

    public FakeScanScorer(List<string> log, Result<ScanResult> result)
    {
        _log    = log;
        _result = result;
    }

    public Result<ScanResult> Score(HardwareInventory hw, SettingsState state, BenchmarkResult? baseline)
    {
        _log.Add(LogTag.Scan);
        return _result;
    }
}

// ── ITweakEngine (recording) ─────────────────────────────────────────────────
// The key orchestration fake: records every plan applied in call order.
// BehaviorForPlan lets a test override the response for any call (by plan content)
// — including throwing — without forcing runner-internal flags.
internal sealed class RecordingTweakEngine : ITweakEngine
{
    private readonly List<string> _log;
    private readonly List<TweakPlan> _plans = new();

    /// <summary>Applied tweak plans in call order. The test reads this to assert
    /// which plans were applied (and in what order) based on what the runner
    /// decided — NOT on a pre-forced flag.</summary>
    public IReadOnlyList<TweakPlan> PlansApplied => _plans;

    /// <summary>Optional per-call override. Receives the plan; may return any
    /// Result or throw. Defaults to returning Ok with one snapshot per tweak ID
    /// when null — giving the runner real snapshots to revert.</summary>
    public Func<TweakPlan, Result<ApplyReport>>? BehaviorForPlan { get; set; }

    public RecordingTweakEngine(List<string> log) => _log = log;

    public Task<Result<ApplyReport>> ApplyAsync(
        TweakPlan plan, BenchmarkResult? baseline, int targetPid, CancellationToken ct)
    {
        _log.Add(LogTag.EngineApply);
        _plans.Add(plan);

        if (BehaviorForPlan is not null)
            return Task.FromResult(BehaviorForPlan(plan));

        // Default: return Ok with one applied snapshot per tweak ID.
        // These snapshots are what the runner collects and later reverts —
        // so the revert-order tests have real data to assert against.
        var outcomes = plan.TweakIds
            .Select(id => new ApplyOutcome(
                id, Applied: true, AutoReverted: false,
                Snapshot: new TweakSnapshot(id, "{}"),
                Error: null))
            .ToList<ApplyOutcome>();

        return Task.FromResult(
            Result<ApplyReport>.Ok(new ApplyReport(outcomes, Verdict: null)));
    }
}

// ── ISafetyManager (recording) ───────────────────────────────────────────────
internal sealed class RecordingSafetyManager : ISafetyManager
{
    private readonly List<string> _log;
    private readonly List<TweakSnapshot> _reverted = new();

    /// <summary>Snapshots passed to RevertAsync, in call order.
    /// The first entry = last applied (LIFO first).</summary>
    public IReadOnlyList<TweakSnapshot> RevertedSnapshots => _reverted;

    /// <summary>Configurable return value for RevertAsync (default Ok).</summary>
    public Result RevertResult { get; set; } = Result.Ok();

    public RecordingSafetyManager(List<string> log) => _log = log;

    public Task<Result> EnsureRestorePointAsync() =>
        Task.FromResult(Result.Ok());

    public Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        _log.Add(LogTag.Revert);
        _reverted.Add(snapshot);
        return Task.FromResult(RevertResult);
    }
}

// ── IAdlxGpuTuning (configurable, recording) ─────────────────────────────────
// IsAvailable and UndervoltResult are set by each test. The runner reads them
// and makes a REAL decision about whether to pass the gate and run the GPU write.
// No flag is forced onto the runner — it decides based on what it reads here.
internal sealed class FakeAdlxGpuTuning : IAdlxGpuTuning
{
    private readonly List<string> _log;

    public FakeAdlxGpuTuning(List<string> log) => _log = log;

    public bool IsAvailable { get; set; }

    public Result<int> UndervoltResult { get; set; } = Result<int>.Ok(50);

    // Models a single tunable GPU at index 0 when available + the undervolt read succeeds.
    public Result<int> ResolveTunableGpuIndex() =>
        IsAvailable && UndervoltResult.IsOk
            ? Result<int>.Ok(0)
            : Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "no tunable AMD GPU (fake).");

    public Result<int> GetUndervoltValue(int gpuIndex)
    {
        _log.Add(LogTag.AdlxProbe);
        return UndervoltResult;
    }

    // Not used by the harness gate (the harness drives its own IAdlxGpuTuningWriter) — fail clean.
    public Result<int> GetGfxMaxFreqMhz(int gpuIndex) =>
        Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "not used by harness");

    // Not used by the harness gate — return Fail clean.
    public Result<int> GetPowerLimitPct(int gpuIndex) =>
        Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "not used by harness");

    public Result<FanCurve> GetFanCurve(int gpuIndex) =>
        Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable, "not used by harness");
}

// ── IBenchmarkRunner ─────────────────────────────────────────────────────────
internal sealed class FakeBenchmarkRunner : IBenchmarkRunner
{
    private readonly List<string> _log;

    /// <summary>Configurable result. Default = Ok with a plausible BenchmarkResult.</summary>
    public Result<BenchmarkResult> BenchResult { get; set; } =
        Result<BenchmarkResult>.Ok(new BenchmarkResult(
            AvgFps: 120.0, P1LowFps: 85.0, P01LowFps: 70.0,
            FrameTimesMs: Array.Empty<double>(),
            EstimatedPcLatencyMs: null,
            MarginOfError: 2.0,
            Runs: 1));

    public FakeBenchmarkRunner(List<string> log) => _log = log;

    public Task<Result<BenchmarkResult>> RunAsync(
        int targetPid, TimeSpan duration, int runs, CancellationToken ct)
    {
        _log.Add(LogTag.Bench);
        return Task.FromResult(BenchResult);
    }
}

// ── IAdlxGpuTuningWriter (recording, configurable) ───────────────────────────
// Tracks every Apply/Revert call in the shared call log so order tests can
// assert the exact micro→micro-revert→bench→perf→bench→perf-revert sequence.
// BehaviorForApply lets a test override the result per call-index (0=micro, 1=perf).
internal sealed class FakeAdlxGpuTuningWriter : IAdlxGpuTuningWriter
{
    private readonly List<string> _log;

    public bool IsAvailable { get; set; } = true;

    /// <summary>Default result for ApplyGfxOffset calls (both micro and perf).</summary>
    public Result<AdlxGfxSnapshot> ApplyGfxOffsetResult { get; set; } =
        Result<AdlxGfxSnapshot>.Ok(new AdlxGfxSnapshot(2700, 0));

    /// <summary>Optional per-call override. Receives the zero-based call index
    /// (0=micro, 1=perf). May return any Result or throw.</summary>
    public Func<int, Result<AdlxGfxSnapshot>>? BehaviorForApply { get; set; }

    /// <summary>Result for RevertGfx (default Ok).</summary>
    public Result RevertGfxResult { get; set; } = Result.Ok();

    /// <summary>Freq ApplyGfxOffset calls (micro + perf). Excludes the voltage-probe
    /// write, counted separately in <see cref="VoltApplyCallCount"/> so existing freq
    /// count assertions stay valid.</summary>
    public int ApplyGfxCallCount { get; private set; }

    /// <summary>Freq RevertGfx calls (micro + perf). Excludes the voltage-probe revert,
    /// counted separately in <see cref="VoltRevertCallCount"/>.</summary>
    public int RevertGfxCallCount { get; private set; }

    /// <summary>Voltage-probe write count (freq offset 0, volt offset != 0).</summary>
    public int VoltApplyCallCount { get; private set; }

    /// <summary>Voltage-probe revert count.</summary>
    public int VoltRevertCallCount { get; private set; }

    /// <summary>Result returned for the voltage-probe write (default Ok).</summary>
    public Result<AdlxGfxSnapshot> VoltApplyResult { get; set; } =
        Result<AdlxGfxSnapshot>.Ok(new AdlxGfxSnapshot(0, 1150));

    private bool _voltRevertPending;

    public FakeAdlxGpuTuningWriter(List<string> log) => _log = log;

    public Result<AdlxGfxSnapshot> ApplyGfxOffset(int gpuIndex, int maxFreqOffsetMhz, int voltOffsetMv)
    {
        // Voltage-probe write: isolated (freq offset 0, volt offset != 0). Counted and
        // tagged separately so the micro/perf freq-write counts are unaffected.
        if (maxFreqOffsetMhz == 0 && voltOffsetMv != 0)
        {
            VoltApplyCallCount++;
            _log.Add(LogTag.VoltWrite);
            if (VoltApplyResult.IsOk) _voltRevertPending = true;
            return VoltApplyResult;
        }
        int callIndex = ApplyGfxCallCount++;
        _log.Add(callIndex == 0 ? LogTag.MicroWrite : LogTag.PerfWrite);
        return BehaviorForApply?.Invoke(callIndex) ?? ApplyGfxOffsetResult;
    }

    public Result RevertGfx(int gpuIndex, AdlxGfxSnapshot original)
    {
        // The voltage-probe revert (its write set _voltRevertPending, and the probe runs
        // LAST so no freq revert interleaves) is counted separately.
        if (_voltRevertPending)
        {
            _voltRevertPending = false;
            VoltRevertCallCount++;
            _log.Add(LogTag.VoltRevert);
            return RevertGfxResult;
        }
        int callIndex = RevertGfxCallCount++;
        _log.Add(callIndex == 0 ? LogTag.MicroRevert : LogTag.PerfRevert);
        return RevertGfxResult;
    }

    // Power methods — not used by the harness passage 5 currently; fail clean.
    public Result<AdlxPowerSnapshot> ApplyPowerLimit(int gpuIndex, int powerLimitPct) =>
        Result<AdlxPowerSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "not used in harness step 5");

    public Result RevertPower(int gpuIndex, AdlxPowerSnapshot original) =>
        Result.Fail(ErrorCodes.TweakSdkUnavailable, "not used in harness step 5");
}

// ── IHarnessCleanupVerifier (recording) ──────────────────────────────────────
internal sealed class FakeCleanupVerifier : IHarnessCleanupVerifier
{
    private readonly List<string> _log;

    public int VerifyCallCount { get; private set; }

    /// <summary>Configurable report returned to the runner.
    /// Default = clean (no service, no task, no residuals).</summary>
    public CleanupVerifyReport Report { get; set; } = new(
        NoServiceInstalled: true,
        NoScheduledTaskInstalled: true,
        ResidualsFound: Array.Empty<string>());

    public FakeCleanupVerifier(List<string> log) => _log = log;

    public Task<CleanupVerifyReport> VerifyAsync(CancellationToken ct)
    {
        _log.Add(LogTag.Cleanup);
        VerifyCallCount++;
        return Task.FromResult(Report);
    }
}
