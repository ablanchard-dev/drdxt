namespace DrDxt.ElevatedHelper.Execution.NvApi;

// -----------------------------------------------------------------------
// NvApiTuningMath — the GPU value-bounds POLICY enforced AT/BELOW the privilege
// boundary (defense n°1). Kept in the helper (not the native leaf) because it MIRRORS
// the App-layer GpuSafeBounds — the recipe's unit conversions (MHz↔kHz, %↔PCM, the
// driver clamp) live with the native boundary in DrDxt.GpuNative (NvApiConvert), this
// file is policy only. Pure + unit-testable without a GPU.
//
// The ELEVATED helper enforces the same conservative band as GpuSafeBounds — it never
// trusts the caller (or the client-side clamp) for value bounds. An offset OUTSIDE the
// band is REJECTED (by OpAllowlist AND by the executor write in NvApiTuningActions),
// never silently clamped, so a raw IPC op (a tools/r2* smoke, or a future App build that
// wires the tuning seams) can never push an out-of-policy offset to live hardware.
//
// GpuSafeBounds (DrDxt.Application) is the single source of truth for the band. The helper
// depends only on DrDxt.Core and cannot reference it, so the constants are MIRRORED here;
// the cross-assembly test NvApiTuningPolicyBoundSyncTests asserts they stay equal to
// GpuSafeBounds.{Min,Max}{Core,Memory}OffsetMhz. Keep them in lock-step.
//
// Pure + total: the checks never throw — no Math.Abs (int.MinValue is safe) and NaN /
// Infinity are rejected by the power rail.
// -----------------------------------------------------------------------
internal static class NvApiTuningMath
{
    // Mirror of GpuSafeBounds.MinCoreOffsetMhz / MaxCoreOffsetMhz (the conservative band).
    internal const int MinCoreOffsetMhz = -200;
    internal const int MaxCoreOffsetMhz = 100;

    // Mirror of GpuSafeBounds.MinMemoryOffsetMhz / MaxMemoryOffsetMhz.
    internal const int MinMemoryOffsetMhz = -500;
    internal const int MaxMemoryOffsetMhz = 500;

    // Power-limit % rail. INTENTIONALLY wider than GpuSafeBounds (60..100%): the power target
    // is also bounded 0..100 by OpAllowlist and driver-clamped to the GPU's reported
    // [min,max] in NvApiConvert.ClampPcm, so this stays the last-resort finite/NaN guard only.
    internal const double MinPowerPercent = 50.0;
    internal const double MaxPowerPercent = 120.0;

    /// <summary>True if a core-clock offset (MHz) is within the policy band
    /// [<see cref="MinCoreOffsetMhz"/>, <see cref="MaxCoreOffsetMhz"/>]. Uses NO Math.Abs,
    /// so int.MinValue can never overflow across the IPC.</summary>
    internal static bool IsCoreOffsetWithinHardRail(int mhz) =>
        mhz >= MinCoreOffsetMhz && mhz <= MaxCoreOffsetMhz;

    /// <summary>True if a memory-clock offset (MHz) is within the policy band
    /// [<see cref="MinMemoryOffsetMhz"/>, <see cref="MaxMemoryOffsetMhz"/>].</summary>
    internal static bool IsMemoryOffsetWithinHardRail(int mhz) =>
        mhz >= MinMemoryOffsetMhz && mhz <= MaxMemoryOffsetMhz;

    /// <summary>True if a power-limit percent is FINITE and within the helper's hard rail.
    /// NaN / Infinity are rejected (they would slip past naive &lt;/&gt; comparisons).</summary>
    internal static bool IsPowerPercentWithinHardRail(double percent) =>
        double.IsFinite(percent) && percent >= MinPowerPercent && percent <= MaxPowerPercent;
}
