namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// NvApiConvert — the pure, unit-testable arithmetic the RISKY NVAPI tuning RECIPE
// uses to express values in the DRIVER's units. It lives in the leaf next to the
// single P/Invoke boundary (NvApiNative) because it is part of the recipe, not the
// safety POLICY: the conservative offset / power bands stay in the helper's
// NvApiTuningMath (mirrored from GpuSafeBounds), this file is conversion-only.
//
//  * Core-clock offset is expressed to the driver in kHz (recipe §A): +100 MHz =
//    100_000 kHz; the sign is preserved for a negative undervolt offset.
//  * Power limit is expressed in per-cent-mille (PCM, recipe §B): 100% = 100_000.
//  * ClampPcm bounds a requested PCM target to the driver's reported [min, max];
//    inverted/garbage bounds return the value unchanged (the caller then fails
//    clean) rather than throwing.
// -----------------------------------------------------------------------
internal static class NvApiConvert
{
    /// <summary>Core-clock offset MHz → kHz (the unit SetPstates20 expects). Sign kept.</summary>
    internal static int MhzToKhz(int mhz) => mhz * 1000;

    /// <summary>Core-clock offset kHz → MHz (truncating toward zero).</summary>
    internal static int KhzToMhz(int khz) => khz / 1000;

    /// <summary>Power-limit percent → per-cent-mille (90% → 90_000).</summary>
    internal static uint PercentToPcm(double percent) => (uint)Math.Round(percent * 1000.0);

    /// <summary>Power-limit per-cent-mille → percent (90_000 → 90).</summary>
    internal static int PcmToPercent(uint pcm) => (int)Math.Round(pcm / 1000.0);

    /// <summary>Clamps a PCM power target to the driver's [min, max]. Inverted bounds
    /// (max &lt; min) are treated as invalid and the value is returned unchanged so the
    /// caller can refuse the write — never throws.</summary>
    internal static uint ClampPcm(uint value, uint min, uint max)
    {
        if (max < min) return value;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
