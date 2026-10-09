using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.ElevatedHelper.Execution.Adlx;

// -----------------------------------------------------------------------
// ADLXTuningActions — the real risky AMD ADLX tuning writes (the m2smoke boundary),
// the AMD mirror of NvApiTuningActions.
//
// It reuses the SINGLE verified native ADLX boundary — AdlxNative in DrDxt.GpuNative
// (live-validated on RX 9070 XT / RDNA4, slots verified vs the AMD ADLX SDK headers).
// There is NO second copy of the vtable slots in the helper (the divergence trap we
// swore off): the helper just orchestrates GET-then-SET over IAdlxNative.
//
// GOLDEN rules, mirrored from the NVAPI path:
//   #1 GET-then-SET: AdlxNative reads the FULL current state, validates the target
//      against the DRIVER's own reported range, and writes only the one knob. We do an
//      explicit GET first (TryReadGfx) to read the current voltage (passed back UNCHANGED)
//      and the DRIVER's MaxFreq hardware range used by the defense-in-depth check below.
//   #2 Defense-in-depth bounds (TWO distinct bands, by design):
//        • The conservative POLICY band [-200,+100] MHz is an OFFSET band and lives at the
//          APP layer (GpuSafeBounds), enforced before the op ever reaches the IPC. The
//          undervolt op carries an absolute MaxFreq (MHz), not an offset, so an offset band
//          is meaningless here and is not re-checked at the helper.
//        • The helper instead validates the absolute target against the DRIVER's own
//          reported hardware range (AdlxGfxReads.MaxFreqRange) and REJECTS (fail-clean,
//          never clamps) anything outside it — so even a compromised caller can never push
//          an out-of-hardware-range MaxFreq to the GPU. The PowerLimit path still mirrors
//          the conservative % band via AdlxTuningMath (that band IS absolute).
//   #3 Any native false / fault → Result.Fail(HELPER.OP_FAILED), never an Ok-on-failure.
//
// On a non-AMD box / when amdadlx64.dll is absent, IAdlxNative.IsAvailable is false and
// every call fails clean. RDNA4 dropped manual voltage control: the Undervolt path never
// changes voltage (it passes the current voltage straight back), so AdlxNative skips the
// voltage write — MaxFreq-only, which is what works live. A2 (07/10/2026) : AntiLag (0/1)
// s'écrit par IADLX3DAntiLag.SetEnabled sur la carte dédiée ; toute autre valeur est refusée.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class ADLXTuningActions : IADLXTuningActions
{
    private const string Unavailable = "AMD ADLX unavailable (no AMD GPU / Adrenalin driver).";

    private readonly IAdlxNative _native;

    public ADLXTuningActions() : this(new AdlxNative()) { }

    internal ADLXTuningActions(IAdlxNative native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    public Result SetTuning(int gpuIndex, AdlxTuningKind kind, int intValue)
    {
        if (gpuIndex < 0)
            return Fail("Invalid GPU index.");
        if (!_native.IsAvailable)
            return Fail(Unavailable);

        try
        {
            return kind switch
            {
                AdlxTuningKind.Undervolt => SetUndervolt(gpuIndex, intValue),
                AdlxTuningKind.PowerLimit => SetPowerLimit(gpuIndex, intValue),

                // A2 (07/10/2026) : Anti-Lag classique, 0 ou 1, sur la carte dédiée (l'index est ignoré : c'est
                // la carte que le lecteur lit, jamais la puce intégrée d'un APU).
                AdlxTuningKind.AntiLag => intValue is 0 or 1
                    ? (_native.EcrireAntiLag(intValue == 1) is { } raison ? Fail(raison) : Result.Ok())
                    : Fail($"Anti-Lag attend 0 ou 1, reçu {intValue}."),
                AdlxTuningKind.FrameRateTarget => intValue != 0
                    ? (_native.EcrireFrtc(intValue) is { } refus ? Fail(refus) : Result.Ok())
                    : Fail("FRTC : 0 n'est ni une cible ni un état."),
                AdlxTuningKind.VerticalRefresh => intValue is >= 0 and <= 3
                    ? (_native.EcrireVsync(intValue) is { } vsyncRefus ? Fail(vsyncRefus) : Result.Ok())
                    : Fail($"Vsync : mode {intValue} inconnu (0 à 3)."),

                // Defensive: an undefined enum value (e.g. an out-of-range cast on the wire)
                // is refused, never a crash, never a false Ok.
                _ => Fail($"Unsupported ADLX tuning kind '{(int)kind}'."),
            };
        }
        catch (Exception ex)
        {
            // The native boundary already swallows faults (returns false); this is the
            // last-resort guard so an exception never crosses the IPC.
            return Fail($"ADLX tuning write failed: {ex.GetType().Name}.");
        }
    }

    // --- Undervolt: an absolute GFX max-clock target (MHz), MaxFreq-only. ---
    // The caller (GpuUndervoltTweak) has already turned the user's conservative OFFSET into
    // an absolute target by reading the current MaxFreq; the inverse op carries the absolute
    // Original. The helper writes the absolute target verbatim (no +offset) — symmetric with
    // SetPowerLimit, so apply→revert restores the exact stock MaxFreq.

    private Result SetUndervolt(int gpuIndex, int targetMaxFreqMhz)
    {
        // GET first: read the current GFX state for (a) the DRIVER's MaxFreq hardware range
        // (the defense-in-depth bound below) and (b) the current voltage, passed back
        // UNCHANGED — on RDNA4 manual voltage is dropped, and AdlxNative skips the voltage
        // write when newVoltMv == current voltage (MaxFreq-only, what works live).
        if (!_native.TryReadGfx(gpuIndex, out var current))
            return Fail("ADLX graphics tuning read failed (GPU not supported or interface absent).");

        // Defense-in-depth: the helper never trusts the caller for bounds. The op carries an
        // Absolute MaxFreq, so the conservative OFFSET policy band [-200,+100] (App-layer
        // GpuSafeBounds) is meaningless here; instead REJECT (fail-clean, never clamp) any
        // target outside the DRIVER's own reported hardware range. Even a compromised caller
        // can never push an out-of-hardware-range MaxFreq to live silicon.
        var range = current.MaxFreqRange;
        if (targetMaxFreqMhz < range.Min || targetMaxFreqMhz > range.Max)
            return Fail(
                $"Refused: ADLX absolute target MaxFreq {targetMaxFreqMhz} MHz is outside the driver's " +
                $"reported hardware range [{range.Min}, {range.Max}] MHz.");

        // SET: AdlxNative re-reads (belt-and-suspenders), validates the target against the
        // driver's GetGPUMaxFrequencyRange, writes MaxFreq, and skips voltage (unchanged).
        if (!_native.TrySetGfx(gpuIndex, targetMaxFreqMhz, current.VoltMv, out _))
            return Fail(
                "ADLX graphics tuning write failed (range violation, unsupported GPU, or driver fault).");

        return Result.Ok();
    }

    // --- PowerLimit: an absolute power-limit (% of TDP). ---

    private Result SetPowerLimit(int gpuIndex, int pct)
    {
        // Defense-in-depth: refuse an out-of-band percent here (reject, never clamp).
        if (!AdlxTuningMath.IsPowerLimitWithinHardRail(pct))
            return Fail(
                $"Refused: ADLX power-limit {pct}% is outside the GPU safe-bounds policy band " +
                $"[{AdlxTuningMath.MinPowerLimitPct}, {AdlxTuningMath.MaxPowerLimitPct}]%.");

        // AdlxNative does GET-then-SET: reads current, validates against the driver's
        // GetPowerLimitRange, writes the power limit.
        if (!_native.TrySetPowerLimit(gpuIndex, pct, out _))
            return Fail(
                "ADLX power-limit write failed (range violation, unsupported GPU, or driver fault).");

        return Result.Ok();
    }

    private static Result Fail(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);
}
