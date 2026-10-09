namespace DrDxt.ElevatedHelper.Execution.Adlx;

// -----------------------------------------------------------------------
// AdlxTuningMath — the pure, unit-testable POWER-LIMIT value-bounds policy enforced
// AT/BELOW the privilege boundary for the ADLX (AMD) power-limit write.
//
// The ELEVATED helper enforces the same conservative band as the App-layer
// GpuSafeBounds — it never trusts the caller (or the client-side clamp) for value
// bounds. A value OUTSIDE the band is REJECTED by ADLXTuningActions (default-DENY,
// fail-clean), never silently clamped, so a raw IPC op (a future App build that wires
// the tuning seams, or a compromised caller) can never push an out-of-policy value to
// live hardware.
//
// Why only the power-limit band lives here: a power limit is an absolute %, so a fixed
// [60,100] policy band is meaningful at the helper and is mirrored from GpuSafeBounds.
// The UNDERVOLT op, by contrast, carries an absolute MaxFreq (MHz) — the conservative
// OFFSET band [-200,+100] is meaningless on an absolute value, so it stays at the App
// layer (GpuSafeBounds) and the helper validates the absolute target against the
// DRIVER's own reported hardware range instead (see ADLXTuningActions.SetUndervolt).
//
// GpuSafeBounds (DrDxt.Application) is the single source of truth for the % band. The
// helper depends only on DrDxt.Core (+ the leaf DrDxt.GpuNative) and cannot reference
// DrDxt.Application, so the constants are MIRRORED here; the cross-assembly test
// AdlxTuningPolicyBoundSyncTests asserts they stay equal to GpuSafeBounds. Keep them in
// lock-step. Pure + total: the check never throws.
// -----------------------------------------------------------------------
internal static class AdlxTuningMath
{
    // Mirror of GpuSafeBounds.MinPowerLimitPct / MaxPowerLimitPct (the conservative
    // absolute power-limit band — never starve the GPU below the floor).
    internal const int MinPowerLimitPct = 60;
    internal const int MaxPowerLimitPct = 100;

    /// <summary>True if an absolute power-limit percent is within the policy band
    /// [<see cref="MinPowerLimitPct"/>, <see cref="MaxPowerLimitPct"/>].</summary>
    internal static bool IsPowerLimitWithinHardRail(int pct) =>
        pct >= MinPowerLimitPct && pct <= MaxPowerLimitPct;
}
