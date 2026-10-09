using DrDxt.Core;

namespace DrDxt.Detection.Gpu.Adlx;

// -----------------------------------------------------------------------
// IAdlxGpuTuningWriter — harness-side ADLX write seam (Task 4).
//
// Lives in Detection, not in the product. The product helper's PrivilegedOpExecutor
// never references this harness-side writer; the product's own ADLX write path is
// ADLXTuningActions (T2/T3). The multi-point fan-curve stays deferred there
// (FanCurveDeferred — E4 fan-write is a dedicated future task).
// The harness runner calls this directly (it runs elevated via UAC).
//
// SAFETY CONTRACT:
//   • IsAvailable == false → every Apply* returns Fail; NO driver call.
//   • Apply* clamps offsets to GpuSafeBounds first (application-level guard).
//   • Apply* validates absolute targets against the hardware's own Get…Range
//     Before any write (driver-level guard). Out-of-range → Fail, no write.
//   • Apply* returns the pre-write snapshot for guaranteed revert.
//   • Revert* restores the original by calling the matching Set method.
//   • Never throws across the boundary; faults surface as Fail results.
// -----------------------------------------------------------------------

/// <summary>Pre-write GFX state captured by TrySetGfx for guaranteed revert.</summary>
public readonly record struct AdlxGfxSnapshot(int MaxFreqMhz, int VoltMv);

/// <summary>Pre-write power state captured by TrySetPowerLimit for guaranteed revert.</summary>
public readonly record struct AdlxPowerSnapshot(int PowerLimitPct);

/// <summary>Harness-side ADLX write boundary. Pure logic over IAdlxNative —
/// fully testable with FakeAdlxNative, no AMD GPU required.</summary>
public interface IAdlxGpuTuningWriter
{
    /// <summary>True when ADLXInitialize succeeded (AMD GPU + Adrenalin driver present).
    /// False → all Apply* return Fail without touching the driver.</summary>
    bool IsAvailable { get; }

    /// <summary>Applies a max-frequency offset + voltage offset to the specified GPU.
    /// Sequence: clamp to GpuSafeBounds → read current (get-then-set) → validate
    /// absolute target against hardware range → set.
    /// Returns the pre-write state as a snapshot for revert. Never throws.</summary>
    Result<AdlxGfxSnapshot> ApplyGfxOffset(int gpuIndex, int maxFreqOffsetMhz, int voltOffsetMv);

    /// <summary>Reverts GFX state to the snapshot captured by a preceding ApplyGfxOffset.
    /// Never throws.</summary>
    Result RevertGfx(int gpuIndex, AdlxGfxSnapshot original);

    /// <summary>Applies a bounded power-limit percent. Clamps to GpuSafeBounds, validates
    /// against hardware range, then sets. Returns original for revert. Never throws.</summary>
    Result<AdlxPowerSnapshot> ApplyPowerLimit(int gpuIndex, int powerLimitPct);

    /// <summary>Reverts power limit to the snapshot captured by a preceding ApplyPowerLimit.
    /// Never throws.</summary>
    Result RevertPower(int gpuIndex, AdlxPowerSnapshot original);
}
