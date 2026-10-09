using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution.Adlx;

// -----------------------------------------------------------------------
// IADLXTuningActions — the RISKY AMD ADLX write seam (M2 Phase E, mirror of
// INvApiTuningActions).
//
// This is the AMD counterpart of INvApiTuningActions: the elevated write path for
// the two ADLX tuning controls DrDXT applies (Option A, master §11):
//   * Undervolt   — an absolute GFX max-clock target (MHz). The caller turns the user's
//                   conservative OFFSET into an absolute target (current MaxFreq + offset)
//                   and the inverse op carries the absolute original, so apply→revert
//                   restores the exact stock MaxFreq (symmetric with PowerLimit). The GPU
//                   runs cooler / more efficiently; voltage is never touched (RDNA4 drops
//                   manual voltage — passing the current voltage back makes AdlxNative skip
//                   the voltage write entirely, MaxFreq-only).
//   * PowerLimit  — an absolute power-limit (% of TDP).
//
// Both follow the GOLDEN GET-then-SET rule (via AdlxNative): read the current state,
// validate the target against the driver's own reported range, write only the one
// knob. The native ADLX boundary is the single verified copy in DrDxt.GpuNative
// (no duplicated vtable slots).
//
// Defense-in-depth — the elevated write enforces bounds itself, never trusting its caller:
//   * Undervolt carries an absolute MaxFreq, so the helper validates it against the
//     DRIVER's own reported hardware range and REFUSES (fail-clean, never clamps) anything
//     outside it. The conservative OFFSET policy band [-200,+100] lives at the App layer
//     (GpuSafeBounds) — an offset band is meaningless on an absolute value.
//   * PowerLimit's % band IS absolute, so it still mirrors GpuSafeBounds via AdlxTuningMath
//     and is REFUSED here if out of band. So a raw IPC op (a future compromised App build)
//     can never push an out-of-policy value to live hardware.
//
// SetTuning returns a Result and never throws across the IPC. On a non-AMD box / when
// ADLX is unavailable / on any driver fault it fails clean (HELPER.OP_FAILED) — never
// a false Ok. Anti-Lag (0 or 1, since 07/10/2026) is written here too; any other kind
// fails clean honestly (§22.3), never a placebo success.
// -----------------------------------------------------------------------

/// <summary>RISKY AMD ADLX tuning seam: the GFX max-clock undervolt offset + the
/// absolute power-limit write (GET-then-SET via AdlxNative), with a helper-side
/// GpuSafeBounds re-check. Fail-clean on a non-AMD box / unavailable ADLX / driver
/// fault / unsupported kind — never throws, never a false Ok.</summary>
public interface IADLXTuningActions
{
    /// <summary>Applies an ADLX tuning value to the GPU at <paramref name="gpuIndex"/>.
    /// <para><see cref="AdlxTuningKind.Undervolt"/> → <paramref name="intValue"/> is an
    /// Absolute GFX max-clock target (MHz); validated against the DRIVER's reported hardware
    /// range (REJECTED if outside, never clamped) and written MaxFreq-only (voltage unchanged).
    /// The conservative OFFSET policy band is enforced earlier, at the App layer.</para>
    /// <para><see cref="AdlxTuningKind.PowerLimit"/> → <paramref name="intValue"/> is an
    /// absolute power-limit (% of TDP); re-checked against the safe band (REJECTED if outside),
    /// driver-range validated.</para>
    /// <para><see cref="AdlxTuningKind.AntiLag"/> / any other kind → fail-clean
    /// (HELPER.OP_FAILED), honest unavailability.</para>
    /// Never throws.</summary>
    Result SetTuning(int gpuIndex, AdlxTuningKind kind, int intValue);
}
