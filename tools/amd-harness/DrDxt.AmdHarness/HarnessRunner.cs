using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.AmdHarness;

// -----------------------------------------------------------------------
// HarnessRunner — portable AMD test-harness orchestrator (Task 2 / Task 4).
//
// Composes EXISTING product bricks through their interfaces; no privileged
// logic lives here. The runner orchestrates; the bricks execute.
//
// INVARIANTS (each tested independently):
//   (a) Steps run in order: detect → read-state → scan → safe-apply →
//       adlx-gate → [micro-write → micro-revert → bench-before →
//       perf-write → bench-after → perf-revert, if gate passed] →
//       revert(safe) → cleanup.
//   (b) GPU write (step 5) runs ONLY when the ADLX read-gate (step 4) passed.
//   (c) REVERT (step 6) and CLEANUP (step 7) ALWAYS execute — even if an
//       earlier step threw an unexpected exception.
//   (d) No permanent service / scheduled task is ever installed.
//   (e) A seam that is unavailable → honest degraded outcome; never a crash,
//       never a silent skip, never a false success.
//   (f) A failing GPU step NEVER prevents the safe steps' revert/cleanup.
//   (g) Safe-tweak revert is LIFO: last applied snapshot reverted first.
//   (h) ADLX GPU writes are self-contained in step 5 via IAdlxGpuTuningWriter:
//       micro-write is ALWAYS reverted before perf-write begins; perf-write
//       is ALWAYS reverted (try/finally) even when bench-after throws.
//       GPU reverts do NOT go through ISafetyManager.
//
// Result pattern throughout; NO exception ever escapes RunAsync.
// -----------------------------------------------------------------------
public sealed class HarnessRunner
{
    private static readonly TimeSpan DefaultBenchDuration = TimeSpan.FromSeconds(8);

    private readonly IHardwareDetector _detector;
    private readonly ISettingsStateReader _stateReader;
    private readonly IScanScorer _scanner;
    private readonly ITweakEngine _engine;
    private readonly ISafetyManager _safety;
    private readonly IAdlxGpuTuning _adlx;
    private readonly IAdlxGpuTuningWriter _writer;
    private readonly IBenchmarkRunner _bench;
    private readonly IHarnessCleanupVerifier _cleanup;
    // Optional read-only diagnostic hook: walks the ADLX GFX read path and reports
    // where it stopped. Called ONLY when the GPU gate is skipped, so a skip on real
    // AMD hardware is fully explained instead of a vague "not available". Never writes.
    private readonly Func<int, IReadOnlyList<GpuProbeStep>>? _gfxDiagnostics;

    public HarnessRunner(
        IHardwareDetector detector,
        ISettingsStateReader stateReader,
        IScanScorer scanner,
        ITweakEngine engine,
        ISafetyManager safety,
        IAdlxGpuTuning adlx,
        IAdlxGpuTuningWriter writer,
        IBenchmarkRunner bench,
        IHarnessCleanupVerifier cleanup,
        Func<int, IReadOnlyList<GpuProbeStep>>? gfxDiagnostics = null)
    {
        _detector = detector;
        _stateReader = stateReader;
        _scanner = scanner;
        _engine = engine;
        _safety = safety;
        _adlx = adlx;
        _writer = writer;
        _bench = bench;
        _cleanup = cleanup;
        _gfxDiagnostics = gfxDiagnostics;
    }

    /// <summary>Runs ONE throwaway test session end-to-end. NEVER throws. Returns a
    /// completed <see cref="HarnessReport"/> that captures every step's outcome
    /// honestly — including degraded states.</summary>
    public async Task<HarnessReport> RunAsync(HarnessConfig config, CancellationToken ct)
    {
        // Snapshots accumulated during apply steps — reversed for LIFO revert.
        var appliedSnapshots = new List<TweakSnapshot>();
        var stepOutcomes = new List<HarnessStepOutcome>();
        HardwareInventory? hw = null;
        ScanResult? scanResult = null;
        var gpuGate = GpuGateOutcome.Skipped;
        IReadOnlyList<GpuProbeStep>? gpuProbe = null;

        // ── Steps 1-5: session body ──────────────────────────────────────────────
        // A single try/catch ensures that an unexpected throw from ANY seam (a seam
        // that violates its "never throw" contract) does NOT bypass revert/cleanup.
        try
        {
            (hw, scanResult, gpuGate, gpuProbe) = await RunSessionCoreAsync(
                config, appliedSnapshots, stepOutcomes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Frontier guard: record the fault, then fall through to revert/cleanup.
            stepOutcomes.Add(new HarnessStepOutcome(
                "session-exception", Succeeded: false,
                $"{ex.GetType().Name}: {ex.Message}"));
        }

        // ── Step 6: REVERT all applied snapshots — ALWAYS, LIFO ─────────────────
        bool revertCompleted = false;
        try
        {
            // LIFO: iterate from the end — the last applied snapshot is reverted first.
            // This mirrors the TweakEngine's own rollback order and correctly undoes
            // composite / order-dependent tweaks.
            for (int i = appliedSnapshots.Count - 1; i >= 0; i--)
            {
                var snap = appliedSnapshots[i];
                Result revertResult;
                try
                {
                    revertResult = await _safety.RevertAsync(snap).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    revertResult = Result.Fail(ErrorCodes.HelperOpFailed,
                        $"Revert threw: {ex.GetType().Name}: {ex.Message}");
                }
                stepOutcomes.Add(new HarnessStepOutcome(
                    $"revert:{snap.TweakId}",
                    Succeeded: revertResult.IsOk,
                    Note: revertResult.IsOk
                        ? null
                        : $"{revertResult.Error?.Code}: {revertResult.Error?.Message}"));
            }
            revertCompleted = true;
        }
        catch (Exception ex)
        {
            stepOutcomes.Add(new HarnessStepOutcome(
                "revert-exception", Succeeded: false,
                $"{ex.GetType().Name}: {ex.Message}"));
        }

        // ── Step 7: CLEANUP — ALWAYS ─────────────────────────────────────────────
        CleanupVerifyReport? cleanupReport = null;
        try
        {
            cleanupReport = await _cleanup.VerifyAsync(ct).ConfigureAwait(false);

            bool clean = cleanupReport.NoServiceInstalled
                         && cleanupReport.NoScheduledTaskInstalled
                         && cleanupReport.ResidualsFound.Count == 0;
            stepOutcomes.Add(new HarnessStepOutcome(
                "cleanup-verify",
                Succeeded: clean,
                Note: clean
                    ? null
                    : BuildCleanupNote(cleanupReport)));
        }
        catch (Exception ex)
        {
            stepOutcomes.Add(new HarnessStepOutcome(
                "cleanup-exception", Succeeded: false,
                $"{ex.GetType().Name}: {ex.Message}"));
        }

        return new HarnessReport(
            IsCompleted: true,
            Hardware: hw,
            ScanResult: scanResult,
            GpuGate: gpuGate,
            StepOutcomes: stepOutcomes,
            RevertCompleted: revertCompleted,
            CleanupReport: cleanupReport,
            GpuProbe: gpuProbe);
    }

    // ── Session body (steps 1-5) ─────────────────────────────────────────────────
    // Extracted so early-return (e.g. no hardware) cleanly skips the remaining
    // session steps without affecting the always-run revert/cleanup above.
    private async Task<(HardwareInventory? Hw, ScanResult? Scan, GpuGateOutcome Gate, IReadOnlyList<GpuProbeStep>? Probe)>
        RunSessionCoreAsync(
            HarnessConfig config,
            List<TweakSnapshot> appliedSnapshots,
            List<HarnessStepOutcome> stepOutcomes,
            CancellationToken ct)
    {
        HardwareInventory? hw = null;
        ScanResult? scanResult = null;
        var gate = GpuGateOutcome.Skipped;
        IReadOnlyList<GpuProbeStep>? gpuProbe = null;

        // ── Step 1: detect hardware ──────────────────────────────────────────────
        var detectResult = await _detector.DetectAsync().ConfigureAwait(false);
        if (!detectResult.IsOk || detectResult.Value is null)
        {
            stepOutcomes.Add(new HarnessStepOutcome("detect", Succeeded: false,
                $"{detectResult.Error?.Code}: {detectResult.Error?.Message}"));
            return (hw, scanResult, gate, gpuProbe);  // degrade: cannot proceed without hw
        }
        hw = detectResult.Value;
        stepOutcomes.Add(new HarnessStepOutcome("detect", Succeeded: true));

        // ── Step 2a: read settings state ─────────────────────────────────────────
        var stateResult = await _stateReader.ReadAsync(hw).ConfigureAwait(false);
        if (!stateResult.IsOk || stateResult.Value is null)
        {
            stepOutcomes.Add(new HarnessStepOutcome("read-state", Succeeded: false,
                $"{stateResult.Error?.Code}: {stateResult.Error?.Message}"));
            return (hw, scanResult, gate, gpuProbe);
        }
        var state = stateResult.Value;
        stepOutcomes.Add(new HarnessStepOutcome("read-state", Succeeded: true));

        // ── Step 2b: scan + score ────────────────────────────────────────────────
        var scoreResult = _scanner.Score(hw, state, baseline: null);
        if (!scoreResult.IsOk || scoreResult.Value is null)
        {
            stepOutcomes.Add(new HarnessStepOutcome("scan", Succeeded: false,
                $"{scoreResult.Error?.Code}: {scoreResult.Error?.Message}"));
            return (hw, scanResult, gate, gpuProbe);
        }
        scanResult = scoreResult.Value;
        stepOutcomes.Add(new HarnessStepOutcome("scan", Succeeded: true));

        // ── Step 3: apply SAFE non-GPU tweaks ───────────────────────────────────
        // Pass null baseline — no game running for the portable test; the engine
        // handles this cleanly (keeps applied, null verdict).
        var safePlan = new TweakPlan(config.SafeTweakIds);
        var safeApply = await _engine
            .ApplyAsync(safePlan, baseline: null, config.TargetPid, ct)
            .ConfigureAwait(false);
        CollectAppliedSnapshots(safeApply, appliedSnapshots);
        stepOutcomes.Add(new HarnessStepOutcome("safe-apply",
            Succeeded: safeApply.IsOk,
            Note: safeApply.IsOk ? null
                : $"{safeApply.Error?.Code}: {safeApply.Error?.Message}"));
        // A failed safe-apply is NOT fatal to the run (we still try the gate and
        // always revert/cleanup). The failure is recorded honestly above.

        // ── Step 4: ADLX read-gate ───────────────────────────────────────────────
        // Read-only probe: if ADLX is available and returns a sane value, the gate
        // passes and step 5 runs. Any failure → skip step 5, honest note, NOT a
        // run failure.
        bool gatePassed;
        if (!_adlx.IsAvailable)
        {
            gatePassed = false;
            stepOutcomes.Add(new HarnessStepOutcome("adlx-gate", Succeeded: false,
                "ADLX SDK not available on this machine — GPU write not validated."));
        }
        else
        {
            var probe = _adlx.GetUndervoltValue(config.GpuIndex);
            gatePassed = probe.IsOk;
            if (gatePassed)
            {
                gate = GpuGateOutcome.Passed;
                stepOutcomes.Add(new HarnessStepOutcome("adlx-gate", Succeeded: true,
                    $"ADLX read ok (current undervolt value = {probe.Value})."));
            }
            else
            {
                stepOutcomes.Add(new HarnessStepOutcome("adlx-gate", Succeeded: false,
                    $"GPU write not validated: {probe.Error?.Code}: {probe.Error?.Message}"));
            }
        }

        // When the gate did NOT pass, capture a granular ADLX diagnostic so a skip on
        // real AMD hardware shows EXACTLY where ADLX stopped (DLL/init → GPU list →
        // tuning services → IsSupported → QI v2 → read) instead of a dead end.
        // Read-only; no-op when no hook was wired; never breaks the run.
        if (!gatePassed && _gfxDiagnostics is not null)
        {
            try { gpuProbe = _gfxDiagnostics(config.GpuIndex); }
            catch { /* diagnostics must never break the run */ }
        }

        // ── Step 5: ADLX GPU write (only when gate passed) ───────────────────────
        // The writer is called DIRECTLY — no ITweakEngine involved for GPU writes.
        // GPU write reverts are self-contained (try/finally below) and do NOT go
        // through ISafetyManager.  Safe-tweak snapshots in appliedSnapshots are
        // completely independent and are reverted by the outer LIFO loop (step 6).
        if (gatePassed)
        {
            var benchDuration = config.BenchDuration ?? DefaultBenchDuration;

            // ── Step 5a: MICRO-WRITE — proves the SET path end-to-end ─────────────
            // Applies the smallest safe clock offset, captures the original, then
            // immediately reverts.  The micro-write and its revert are isolated in a
            // try/finally: even if the write throws, the finally is a no-op (nothing
            // was written when the exception occurred before the assignment).
            AdlxGfxSnapshot? microOriginal = null;
            bool microWriteOk = false;
            try
            {
                var microResult = _writer.ApplyGfxOffset(
                    config.GpuIndex, config.MicroFreqOffsetMhz, voltOffsetMv: 0);
                if (microResult.IsOk)
                {
                    microOriginal = microResult.Value;
                    microWriteOk  = true;
                    stepOutcomes.Add(new HarnessStepOutcome("micro-write", Succeeded: true,
                        $"SET MaxFreq offset={config.MicroFreqOffsetMhz} MHz — SET path confirmed."));
                }
                else
                {
                    stepOutcomes.Add(new HarnessStepOutcome("micro-write", Succeeded: false,
                        $"{microResult.Error?.Code}: {microResult.Error?.Message}"));
                }
            }
            catch (Exception ex)
            {
                stepOutcomes.Add(new HarnessStepOutcome("micro-write", Succeeded: false,
                    $"Threw: {ex.GetType().Name}: {ex.Message}"));
            }
            finally
            {
                // ALWAYS revert if micro-write actually wrote (microOriginal != null).
                // This completes BEFORE perf-write begins (sequential try/finally).
                if (microOriginal.HasValue)
                {
                    var rv = _writer.RevertGfx(config.GpuIndex, microOriginal.Value);
                    stepOutcomes.Add(new HarnessStepOutcome("micro-write-revert",
                        Succeeded: rv.IsOk,
                        Note: rv.IsOk ? null : $"{rv.Error?.Code}: {rv.Error?.Message}"));
                }
            }

            // ── Step 5b: PERF-WRITE — conservative offset, measured ──────────────
            // Only runs when micro-write confirmed the SET path works.
            if (microWriteOk)
            {
                // Bench BEFORE perf-write (baseline in steady state, after micro revert)
                BenchmarkResult? beforeBench = null;
                var benchBefore = await _bench
                    .RunAsync(config.TargetPid, benchDuration, runs: 1, ct)
                    .ConfigureAwait(false);
                if (benchBefore.IsOk && benchBefore.Value is not null)
                {
                    beforeBench = benchBefore.Value;
                    stepOutcomes.Add(new HarnessStepOutcome("bench-before-perf", Succeeded: true,
                        $"avgFps={beforeBench.AvgFps:F1} p1Low={beforeBench.P1LowFps:F1}"));
                }
                else
                {
                    stepOutcomes.Add(new HarnessStepOutcome("bench-before-perf", Succeeded: false,
                        $"Bench failed ({benchBefore.Error?.Code}) — proceeding without before measurement."));
                }

                AdlxGfxSnapshot? perfOriginal = null;
                try
                {
                    var perfResult = _writer.ApplyGfxOffset(
                        config.GpuIndex, config.PerfFreqOffsetMhz, voltOffsetMv: 0);
                    if (perfResult.IsOk)
                    {
                        perfOriginal = perfResult.Value;
                        stepOutcomes.Add(new HarnessStepOutcome("perf-write", Succeeded: true,
                            $"SET MaxFreq offset={config.PerfFreqOffsetMhz} MHz."));

                        // Bench AFTER perf-write
                        var benchAfter = await _bench
                            .RunAsync(config.TargetPid, benchDuration, runs: 1, ct)
                            .ConfigureAwait(false);
                        if (benchAfter.IsOk && benchAfter.Value is not null)
                        {
                            var after = benchAfter.Value;
                            string delta = beforeBench is not null
                                ? $" (delta avgFps={after.AvgFps - beforeBench.AvgFps:+0.0;-0.0})"
                                : "";
                            stepOutcomes.Add(new HarnessStepOutcome("bench-after-perf", Succeeded: true,
                                $"avgFps={after.AvgFps:F1} p1Low={after.P1LowFps:F1}{delta}"));
                        }
                        else
                        {
                            stepOutcomes.Add(new HarnessStepOutcome("bench-after-perf", Succeeded: false,
                                $"Bench failed after perf-write ({benchAfter.Error?.Code})."));
                        }
                    }
                    else
                    {
                        stepOutcomes.Add(new HarnessStepOutcome("perf-write", Succeeded: false,
                            $"{perfResult.Error?.Code}: {perfResult.Error?.Message}"));
                    }
                }
                catch (Exception ex)
                {
                    stepOutcomes.Add(new HarnessStepOutcome("perf-write", Succeeded: false,
                        $"Threw: {ex.GetType().Name}: {ex.Message}"));
                }
                finally
                {
                    // ALWAYS revert perf-write if it actually wrote — even if bench-after threw.
                    if (perfOriginal.HasValue)
                    {
                        var rv = _writer.RevertGfx(config.GpuIndex, perfOriginal.Value);
                        stepOutcomes.Add(new HarnessStepOutcome("perf-write-revert",
                            Succeeded: rv.IsOk,
                            Note: rv.IsOk ? null : $"{rv.Error?.Code}: {rv.Error?.Message}"));
                    }
                }
            }

            // ── Step 5c: VOLTAGE-PROBE — does this GPU HONOR SetGPUVoltage? ─────────
            // RDNA3 honors it (real voltage/undervolt path); RDNA4 silently IGNORES
            // voltage writes (MaxFreq-only). Write a tiny NEGATIVE (undervolt) offset,
            // read back to see if it took, then ALWAYS revert. Undervolt direction +
            // immediate revert + no load = safe; the write is validated against the
            // hardware voltage range inside ApplyGfxOffset. The read-back is what turns a
            // silent "OK" on RDNA4 into an honest "not honored" instead of a false pass.
            int? voltBefore = ReadVoltMv(config.GpuIndex);
            AdlxGfxSnapshot? voltOriginal = null;
            try
            {
                var vResult = _writer.ApplyGfxOffset(config.GpuIndex, maxFreqOffsetMhz: 0, config.MicroVoltOffsetMv);
                if (vResult.IsOk)
                {
                    voltOriginal = vResult.Value;
                    int? voltAfter = ReadVoltMv(config.GpuIndex);
                    bool honored = voltBefore is int vb && voltAfter is int va && va != vb;
                    stepOutcomes.Add(new HarnessStepOutcome("voltage-probe", Succeeded: true,
                        $"SetGPUVoltage {config.MicroVoltOffsetMv} mV; read-back {Fmt(voltBefore)}->{Fmt(voltAfter)} mV — " +
                        (honored
                            ? "HONORED (real voltage path on this GPU)."
                            : "not honored (driver ignored the voltage write — MaxFreq-only path).")));
                }
                else
                {
                    stepOutcomes.Add(new HarnessStepOutcome("voltage-probe", Succeeded: false,
                        $"{vResult.Error?.Code}: {vResult.Error?.Message}"));
                }
            }
            catch (Exception ex)
            {
                stepOutcomes.Add(new HarnessStepOutcome("voltage-probe", Succeeded: false,
                    $"Threw: {ex.GetType().Name}: {ex.Message}"));
            }
            finally
            {
                // ALWAYS revert the voltage write if it actually wrote.
                if (voltOriginal.HasValue)
                {
                    var rv = _writer.RevertGfx(config.GpuIndex, voltOriginal.Value);
                    int? voltRestored = ReadVoltMv(config.GpuIndex);
                    stepOutcomes.Add(new HarnessStepOutcome("voltage-probe-revert",
                        Succeeded: rv.IsOk,
                        Note: rv.IsOk
                            ? $"restored to {Fmt(voltRestored)} mV."
                            : $"{rv.Error?.Code}: {rv.Error?.Message}"));
                }
            }
        }

        return (hw, scanResult, gate, gpuProbe);
    }

    // Read the current GPU voltage (mV) for the voltage-probe read-back. Read-only;
    // returns null when the read fails (so the probe reports "?" instead of a fake number).
    private int? ReadVoltMv(int gpuIndex)
    {
        var r = _adlx.GetUndervoltValue(gpuIndex);
        return r.IsOk ? r.Value : (int?)null;
    }

    private static string Fmt(int? v) => v.HasValue ? v.Value.ToString() : "?";

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Collects snapshots from applied, non-auto-reverted outcomes.
    /// Auto-reverted outcomes are already undone by the engine (regression gate);
    /// collecting them again would double-revert them.</summary>
    private static void CollectAppliedSnapshots(
        Result<ApplyReport> applyResult,
        List<TweakSnapshot> collected)
    {
        if (!applyResult.IsOk || applyResult.Value is null)
            return;
        foreach (var o in applyResult.Value.Outcomes)
        {
            if (o.Applied && !o.AutoReverted && o.Snapshot is not null)
                collected.Add(o.Snapshot);
        }
    }

    private static string BuildCleanupNote(CleanupVerifyReport r)
    {
        var parts = new List<string>();
        if (!r.NoServiceInstalled)   parts.Add("service found");
        if (!r.NoScheduledTaskInstalled) parts.Add("scheduled task found");
        if (r.ResidualsFound.Count > 0)
            parts.Add($"residuals: {string.Join(", ", r.ResidualsFound)}");
        return string.Join("; ", parts);
    }
}
