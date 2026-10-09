using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;

namespace DrDxt.App.Profiles;

// -----------------------------------------------------------------------
// M6 — GpuTuningApplyStep: the dedicated, isolated GPU-tuning apply action.
//
// Why it exists (the whole reason GPU tuning was deferred out of the scan→apply batch):
// the TweakEngine batch is all-or-nothing (a single apply failure LIFO-reverts the whole
// batch). If GPU tuning shared the batch with the safe power-plan / latency / X3D fixes, a
// GPU failure / measured regression / instability would roll those safe fixes back too. So
// the GPU selections run here as their own engine batch, completely SEPARATE from the safe
// one — a GPU failure never touches the safe batch.
//
// Reuses the R2 stability/revert machinery as-is, not reinvented:
//   * the engine's batch transaction (LIFO rollback on a mid-batch apply failure →
//     ENGINE.BATCH_REVERTED) and its A/B re-bench → measured-regression auto-revert;
//   * the GpuUndervoltTweak's own stability test (IStabilityTester / the real D3D11 load
//     probe → device-lost detection) that self-reverts an unstable offset immediately.
//   NOTE (honest status): the held-offset lease/heartbeat dead-man's-switch
//   (EstablishAutoRevertLeaseAsync) is not yet armed in this live apply path — it exists +
//   is unit-tested, but no production path calls it today, so a held offset's only safety
//   net is the inline stability self-revert above + NVAPI offset volatility (a reboot
//   restores stock clocks). Arming the lease is the lease note in GpuUndervoltTweak
//   (wire it when the NVAPI/ADLX tuning seams go live).
// Because this is a separate engine batch, every one of those reverts touches only the GPU
// change.
//
// Privileged write path: the GPU tweaks write through the existing elevated client
// (IElevatedClient → the handshake helper → the R2 NVAPI/ADLX executor). In the App graph
// that client is the real signed-IPC one (RegisterRealSeams), so the write PATH is live.
// Read seams: Both vendors are now real. The ADLX (AMD) read seam is AdlxGpuTuningReader
// (T5, validated on RX 9070 XT) and the NVAPI (NVIDIA) read seam is NvApiGpuTuningReader
// (N4; the NVAPI write was live-validated on a 4060) — so on a real AMD OR NVIDIA machine
// the undervolt/power-limit apply path FIRES: the value is read, the absolute/offset target
// written through the helper, then guarded by both the tweak's own stability self-revert
// (IStabilityTester → TWEAK.UNSTABLE) AND this engine batch's A/B re-bench measured-regression
// auto-revert — every revert touching only the GPU change. On a machine with NEITHER vendor
// present the read seam reports unavailable and the apply fails CLEAN (TWEAK.SDK_UNAVAILABLE →
// never a faked success). (INvApiInfo stays a fail-clean stub — unconsumed; the Blackwell
// voltage gate reads architecture from the scan, and V1 is offset-only.)
//
// Conservative + honest: opt-in (only the explicit GPU command calls this, never a scan);
// an empty GPU plan (unsupported GPU / nothing recommended) is a safe no-op; and the live
// targets are RE-CLAMPED to GpuSafeBounds here defensively even though the consumer already
// clamped them at the front door — nothing out of band can leave this step.
// -----------------------------------------------------------------------
public interface IGpuTuningApplyStep
{
    /// <summary>Applies the profile's clamped GPU selections as their own isolated engine
    /// batch, separate from the safe non-GPU batch. An empty GPU plan → safe no-op (Ok, empty
    /// report). Never throws; a GPU failure / MEASURED regression auto-reverts only the GPU
    /// change (reverts only on a measured regression — a null/failed re-bench keeps the batch,
    /// with the restore point + dashboard revert as the backstop).</summary>
    Task<Result<ApplyReport>> ApplyAsync(
        ScanProfileConsumption consumption, BenchmarkResult? baseline, int targetPid, CancellationToken ct);
}

public sealed class GpuTuningApplyStep : IGpuTuningApplyStep
{
    private readonly ITweakEngine _engine;
    private readonly IGpuTuningTargetSource _targets;

    public GpuTuningApplyStep(ITweakEngine engine, IGpuTuningTargetSource targets)
    {
        _engine = engine;
        _targets = targets;
    }

    public async Task<Result<ApplyReport>> ApplyAsync(
        ScanProfileConsumption consumption, BenchmarkResult? baseline, int targetPid, CancellationToken ct)
    {
        // Unsupported GPU / nothing recommended → safe no-op. Never an error, never a write.
        if (consumption is null || !consumption.HasGpuPlan)
            return Result<ApplyReport>.Ok(new ApplyReport(Array.Empty<ApplyOutcome>(), Verdict: null));

        // Defense in depth: re-clamp the live targets to GpuSafeBounds before applying, even
        // though the consumer already clamped them at the front door. Nothing out of band can
        // leave this step (the GPU tweaks then re-gate once more before any privileged write).
        ReclampLiveTargetsDefensively();

        // Run the GPU plan as its own engine batch — wholly SEPARATE from the safe (non-GPU)
        // batch. Any GPU apply failure / measured regression / instability is reverted within
        // This batch only (engine LIFO rollback + the undervolt's stability self-revert); the
        // safe batch was a different engine call and is never touched.
        return await _engine.ApplyAsync(consumption.GpuPlan, baseline, targetPid, ct).ConfigureAwait(false);
    }

    private void ReclampLiveTargetsDefensively()
    {
        var tuning = _targets.Tuning;
        var power = _targets.PowerLimit;

        var safeTuning = tuning with
        {
            CoreOffsetMhz = GpuSafeBounds.ClampCoreOffsetMhz(tuning.CoreOffsetMhz),
            MemoryOffsetMhz = GpuSafeBounds.ClampMemoryOffsetMhz(tuning.MemoryOffsetMhz),
        };
        var safePower = power with { PowerLimitPct = GpuSafeBounds.ClampPowerLimitPct(power.PowerLimitPct) };

        _targets.Set(safeTuning, safePower);
    }
}
