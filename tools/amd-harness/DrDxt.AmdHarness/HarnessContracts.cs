using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.AmdHarness;

// -----------------------------------------------------------------------
// Portable AMD test-harness contracts (Task 2).
//
// HarnessRunner orchestrates ONE throwaway session on a friend's AMD machine:
//   1. detect hardware            (IHardwareDetector)
//   2. read settings + scan       (ISettingsStateReader, IScanScorer)
//   3. apply SAFE non-GPU tweaks  (ITweakEngine)
//   4. ADLX read-gate             (IAdlxGpuTuning — read-only probe)
//   5. ADLX write (gate passed?)  (IBenchmarkRunner baseline → ITweakEngine)
//   6. REVERT all applied tweaks  (ISafetyManager — ALWAYS, LIFO)
//   7. CLEANUP + verify footprint (IHarnessCleanupVerifier — ALWAYS)
//
// Everything flows through EXISTING product interfaces. No new privileged
// logic lives here; this is a pure orchestrator.
// -----------------------------------------------------------------------

/// <summary>Configures one harness session: which tweak IDs to apply per phase,
/// which GPU index to probe, which process to bench against (0 = no game).</summary>
public sealed record HarnessConfig(
    /// <summary>IDs of the SAFE, non-GPU tweaks to apply in step 3
    /// (power-plan, latency, X3D fix). Passed verbatim to ITweakEngine.</summary>
    IReadOnlyList<string> SafeTweakIds,
    /// <summary>IDs of the GPU tuning tweaks (kept for future use; step 5 now calls
    /// the ADLX writer directly rather than routing through ITweakEngine).</summary>
    IReadOnlyList<string> GpuTweakIds,
    /// <summary>AMD GPU index for the ADLX read-gate probe (0 = first AMD GPU).</summary>
    int GpuIndex = 0,
    /// <summary>Target process PID passed to IBenchmarkRunner and ITweakEngine.
    /// 0 = no game running — bench will fail clean and baseline = null.</summary>
    int TargetPid = 0,
    /// <summary>Duration of each benchmark pass.</summary>
    TimeSpan? BenchDuration = null,
    /// <summary>Tiny clock offset for the micro-write (proves the SET path works).
    /// Must fit within GpuSafeBounds.MinCoreOffsetMhz / MaxCoreOffsetMhz.
    /// Default = -15 MHz (smallest safe step, well inside hardware range).</summary>
    int MicroFreqOffsetMhz = -15,
    /// <summary>Conservative POSITIVE clock offset for the perf-write (the real tuning
    /// test): a small OVERCLOCK to measure a GAIN. A negative offset would downclock the
    /// GPU and measure a LOSS — pointless for an optimization tool. Clamped to GpuSafeBounds
    /// before reaching the driver. Default = +50 MHz (modest, well inside the safe band;
    /// mirrors the +75 MHz sweet spot proven on the NVIDIA 4060). The harness ALWAYS reverts
    /// it afterwards — it never persists an offset (no stability gate needed: nothing kept).</summary>
    int PerfFreqOffsetMhz = 50,
    /// <summary>Tiny NEGATIVE voltage offset (mV) for the voltage-probe (step 5c):
    /// proves whether the driver HONORS SetGPUVoltage on this architecture — RDNA3 does,
    /// RDNA4 silently ignores it (MaxFreq-only). Undervolt direction is the safe one;
    /// validated against the hardware voltage range inside ApplyGfxOffset; ALWAYS reverted
    /// immediately, no load. Default = -10 mV.</summary>
    int MicroVoltOffsetMv = -10);

/// <summary>Outcome of the ADLX read-gate (step 4).</summary>
public enum GpuGateOutcome
{
    /// <summary>The read-only probe succeeded — step 5 (GPU write) was attempted.</summary>
    Passed,
    /// <summary>ADLX unavailable or the probe returned an error — step 5 was skipped
    /// and an honest reason is recorded. This is NOT a failure of the whole run.</summary>
    Skipped,
}

/// <summary>Per-step outcome captured in the harness report.</summary>
public sealed record HarnessStepOutcome(string StepId, bool Succeeded, string? Note = null);

/// <summary>Verification report produced by <see cref="IHarnessCleanupVerifier"/>:
/// confirms that no permanent footprint was left behind by the session.</summary>
public sealed record CleanupVerifyReport(
    /// <summary>True when no DrDXT Windows service is installed after the session.</summary>
    bool NoServiceInstalled,
    /// <summary>True when no DrDXT scheduled task is installed after the session.</summary>
    bool NoScheduledTaskInstalled,
    /// <summary>Any unexpected artefacts still present after cleanup (empty = clean).</summary>
    IReadOnlyList<string> ResidualsFound);

/// <summary>Full report returned by <see cref="HarnessRunner.RunAsync"/>.</summary>
public sealed record HarnessReport(
    /// <summary>True when the runner completed all 7 phases (including revert +
    /// cleanup). True even when the GPU gate was skipped or a step failed — only
    /// false if the runner itself has an internal logic error (should never happen).</summary>
    bool IsCompleted,
    /// <summary>Detected hardware; null when detection itself failed.</summary>
    HardwareInventory? Hardware,
    /// <summary>Scan result; null when scan failed or detection failed before it.</summary>
    ScanResult? ScanResult,
    /// <summary>Outcome of the ADLX read-gate (Passed / Skipped).</summary>
    GpuGateOutcome GpuGate,
    /// <summary>Per-step outcomes in execution order (honest even for failures).</summary>
    IReadOnlyList<HarnessStepOutcome> StepOutcomes,
    /// <summary>True when the revert loop ran to completion (even if individual
    /// reverts returned Fail — those are recorded as step outcomes).</summary>
    bool RevertCompleted,
    /// <summary>Cleanup verification result; null if the verifier itself threw.</summary>
    CleanupVerifyReport? CleanupReport,
    /// <summary>ADLX GFX read-path diagnostic, populated only when the GPU gate is
    /// SKIPPED, so a skip on real AMD hardware shows EXACTLY where ADLX stopped.
    /// Null when the gate passed or no diagnostic hook was wired.</summary>
    IReadOnlyList<GpuProbeStep>? GpuProbe = null);

/// <summary>
/// Verifies that the harness session left NO permanent footprint: no Windows
/// service, no scheduled task, no residual artefacts. The production implementation
/// inspects the real SCM / Task Scheduler; the test uses a recording fake.
///
/// CARDINAL INVARIANT: called on EVERY run regardless of earlier step outcomes; the
/// result is ALWAYS captured in the HarnessReport; found services/tasks are surfaced
/// honestly — never hidden. Never throws; genuine inspection failure returns a report
/// with the found artefacts.
/// </summary>
public interface IHarnessCleanupVerifier
{
    Task<CleanupVerifyReport> VerifyAsync(CancellationToken ct);
}
