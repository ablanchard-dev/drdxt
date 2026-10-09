using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxGpuTuningWriter — implements IAdlxGpuTuningWriter over IAdlxNative.
//
// Pure domain logic: offset clamping, range validation, get-then-set
// sequencing. Contains NO native calls — all P/Invoke is in AdlxNative.
// Fully testable against FakeAdlxNative without AMD hardware.
//
// Write RECIPE (vtable slots verified against ADLX SDK headers):
//   GFX writes: IADLXManualGraphicsTuning2
//     Read  : slots 3/4/6/7/9/10 (ranges + current values, via TryReadGfx)
//     Write : slot  8 = SetGPUMaxFrequency
//             slot 11 = SetGPUVoltage
//   Power writes: IADLXManualPowerTuning
//     Read  : slots 3/4 (range + current, via TryReadPower)
//     Write : slot  5 = SetPowerLimit
//
// Validate-then-set sequence enforced here:
//   1. Clamp requested offset to GpuSafeBounds (application-level safety net)
//   2. TryReadGfx/TryReadPower — the "get" in get-then-set; captures current + ranges
//   3. Compute absolute target = current + clamped offset
//   4. Validate absolute target against hardware GetRange (driver-reported band)
//   5. TrySetGfx/TrySetPowerLimit — the "set"; also re-validates internally (belt-and-suspenders)
//   6. Return pre-write snapshot for guaranteed revert
// -----------------------------------------------------------------------
public sealed class AdlxGpuTuningWriter : IAdlxGpuTuningWriter
{
    private readonly IAdlxNative _native;

    public AdlxGpuTuningWriter(IAdlxNative native) => _native = native;

    public bool IsAvailable => _native.IsAvailable;

    // ── GFX (clock + voltage) ─────────────────────────────────────────────

    public Result<AdlxGfxSnapshot> ApplyGfxOffset(int gpuIndex, int maxFreqOffsetMhz, int voltOffsetMv)
    {
        if (gpuIndex < 0)
            return FailGfx($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return FailGfx("ADLX not available (no AMD GPU or driver).");

        // Step 1: clamp to application safety bounds
        int clampedFreqOffset = GpuSafeBounds.ClampCoreOffsetMhz(maxFreqOffsetMhz);
        // Voltage: no separate application bound — hardware range is the only gate
        int clampedVoltOffset = voltOffsetMv;

        // Step 2: read current state (the "get" in get-then-set)
        if (!_native.TryReadGfx(gpuIndex, out var current))
            return FailGfx("Cannot read current GFX state before write.");

        // Step 3: compute absolute targets
        int targetMaxFreq = current.MaxFreqMhz + clampedFreqOffset;
        int targetVolt    = current.VoltMv     + clampedVoltOffset;

        // Step 4: validate absolute targets against hardware-reported range
        if (targetMaxFreq < current.MaxFreqRange.Min || targetMaxFreq > current.MaxFreqRange.Max)
            return FailGfx(
                $"Target MaxFreq {targetMaxFreq} MHz out of hardware range " +
                $"[{current.MaxFreqRange.Min}, {current.MaxFreqRange.Max}].");
        if (targetVolt < current.VoltRange.Min || targetVolt > current.VoltRange.Max)
            return FailGfx(
                $"Target Volt {targetVolt} mV out of hardware range " +
                $"[{current.VoltRange.Min}, {current.VoltRange.Max}].");

        // Step 5: set — TrySetGfx also reads current internally (belt-and-suspenders)
        // and captures the actual pre-write state in 'original'
        if (!_native.TrySetGfx(gpuIndex, targetMaxFreq, targetVolt, out var original))
            return FailGfx("SetGfx rejected by driver (range violation or fault).");

        // Step 6: return pre-write snapshot
        return Result<AdlxGfxSnapshot>.Ok(new AdlxGfxSnapshot(original.MaxFreqMhz, original.VoltMv));
    }

    public Result RevertGfx(int gpuIndex, AdlxGfxSnapshot original)
    {
        if (gpuIndex < 0)
            return Result.Fail(ErrorCodes.TweakSdkUnavailable, $"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Result.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX not available.");

        // Restore original values. Range validation inside TrySetGfx should always pass
        // (these were valid values when captured). Discard the 'original' out-param here.
        bool ok = _native.TrySetGfx(gpuIndex, original.MaxFreqMhz, original.VoltMv, out _);
        return ok
            ? Result.Ok()
            : Result.Fail(ErrorCodes.TweakSdkUnavailable,
                $"RevertGfx failed: driver rejected restore to " +
                $"MaxFreq={original.MaxFreqMhz} MHz, Volt={original.VoltMv} mV.");
    }

    // ── Power limit ───────────────────────────────────────────────────────

    public Result<AdlxPowerSnapshot> ApplyPowerLimit(int gpuIndex, int powerLimitPct)
    {
        if (gpuIndex < 0)
            return FailPwr($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return FailPwr("ADLX not available (no AMD GPU or driver).");

        // Step 1: clamp to application safety bounds
        int clamped = GpuSafeBounds.ClampPowerLimitPct(powerLimitPct);

        // Step 2: read current state (get-then-set)
        if (!_native.TryReadPower(gpuIndex, out var current))
            return FailPwr("Cannot read current power state before write.");

        // Step 3/4: validate clamped value against hardware range
        if (clamped < current.PowerRange.Min || clamped > current.PowerRange.Max)
            return FailPwr(
                $"Target power limit {clamped}% out of hardware range " +
                $"[{current.PowerRange.Min}, {current.PowerRange.Max}].");

        // Step 5: set
        if (!_native.TrySetPowerLimit(gpuIndex, clamped, out var original))
            return FailPwr("SetPowerLimit rejected by driver.");

        // Step 6: return pre-write snapshot
        return Result<AdlxPowerSnapshot>.Ok(new AdlxPowerSnapshot(original.PowerLimitPct));
    }

    public Result RevertPower(int gpuIndex, AdlxPowerSnapshot original)
    {
        if (gpuIndex < 0)
            return Result.Fail(ErrorCodes.TweakSdkUnavailable, $"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Result.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX not available.");

        bool ok = _native.TrySetPowerLimit(gpuIndex, original.PowerLimitPct, out _);
        return ok
            ? Result.Ok()
            : Result.Fail(ErrorCodes.TweakSdkUnavailable,
                $"RevertPower failed: driver rejected restore to {original.PowerLimitPct}%.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Result<AdlxGfxSnapshot> FailGfx(string msg) =>
        Result<AdlxGfxSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, msg);

    private static Result<AdlxPowerSnapshot> FailPwr(string msg) =>
        Result<AdlxPowerSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, msg);
}
