namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// M2 Phase E1 — GpuSafeBounds (conservative bounds table).
//
// This is the guard-rail that sits in front of the elevated helper for every GPU
// tuning tweak (E2 undervolt, E3 power-limit, E4 fan-curve). NO value outside
// these bounds can ever reach the driver: a tweak clamps via Clamp*, and rejects
// (TWEAK.NOT_APPLICABLE) anything that Is*Safe says is out of band.
//
// Values are deliberately TIMID for M2 — we capture the safe, boring 80% of the
// gain (cooler, steadier 1% lows) and refuse the risky edge. They are hardened
// later with real team data (master §10). Pure, side-effect-free, fully tested.
//
// Option A (master §11): a core-clock OFFSET, either sign, never a voltage. A negative
// offset makes each voltage point run slower (cooler, fewer frames), not « the same clocks
// at lower voltage » as this comment once said (corrected 24/09/2026). We bound the core
// offset to a conservative band so neither extreme can reach the helper.
// -----------------------------------------------------------------------
public static class GpuSafeBounds
{
    // --- Core clock offset (MHz). Negative = undervolt direction (Option A). ---
    // Timid band: a modest undervolt is allowed; a small positive offset is
    // tolerated, but nothing aggressive in either direction.
    public const int MinCoreOffsetMhz = -200;
    public const int MaxCoreOffsetMhz = 100;

    // --- Memory clock offset (MHz). Conservative either side of stock. ---
    public const int MinMemoryOffsetMhz = -500;
    public const int MaxMemoryOffsetMhz = 500;

    // --- Power limit (% of TDP). Never starve the GPU below this floor. ---
    public const int MinPowerLimitPct = 60;
    public const int MaxPowerLimitPct = 100;

    // --- Fan curve. Never run silent when the GPU is hot (anti-overheat). ---
    public const int HotTempThresholdC = 80;
    public const int MinFanDutyAtHotTempPct = 40;

    /// <summary>True when the core-clock offset is within the conservative band.
    /// Out of band → the tweak must refuse (TWEAK.NOT_APPLICABLE), never write it.</summary>
    public static bool IsCoreOffsetSafe(int offsetMhz) =>
        offsetMhz >= MinCoreOffsetMhz && offsetMhz <= MaxCoreOffsetMhz;

    /// <summary>True when the memory-clock offset is within the conservative band.</summary>
    public static bool IsMemoryOffsetSafe(int offsetMhz) =>
        offsetMhz >= MinMemoryOffsetMhz && offsetMhz <= MaxMemoryOffsetMhz;

    /// <summary>Clamps a requested core-clock offset into [MinCoreOffsetMhz, MaxCoreOffsetMhz].
    /// The front-door clamp for a per-chip profile parameter (M6): a value out of band is
    /// pulled to the nearest conservative bound so nothing out of band can reach a target.</summary>
    public static int ClampCoreOffsetMhz(int offsetMhz) =>
        Math.Clamp(offsetMhz, MinCoreOffsetMhz, MaxCoreOffsetMhz);

    /// <summary>Clamps a requested memory-clock offset into [MinMemoryOffsetMhz, MaxMemoryOffsetMhz].</summary>
    public static int ClampMemoryOffsetMhz(int offsetMhz) =>
        Math.Clamp(offsetMhz, MinMemoryOffsetMhz, MaxMemoryOffsetMhz);

    /// <summary>Clamps a requested power-limit percent into [MinPowerLimitPct, 100].
    /// Below the floor is raised to the floor (never starve the GPU); above 100 is
    /// capped at 100.</summary>
    public static int ClampPowerLimitPct(int requestedPct) =>
        Math.Clamp(requestedPct, MinPowerLimitPct, MaxPowerLimitPct);

    /// <summary>True when the power-limit percent is within [MinPowerLimitPct, 100].</summary>
    public static bool IsPowerLimitSafe(int pct) =>
        pct >= MinPowerLimitPct && pct <= MaxPowerLimitPct;

    /// <summary>Clamps a fan curve so every point at or above the hot-temperature
    /// threshold has at least MinFanDutyAtHotTempPct duty. Cool points (and points
    /// already above the floor) are left untouched.</summary>
    public static FanCurve ClampFanCurve(FanCurve curve)
    {
        var clamped = curve.Points
            .Select(p => p.TempC >= HotTempThresholdC && p.DutyPct < MinFanDutyAtHotTempPct
                ? p with { DutyPct = MinFanDutyAtHotTempPct }
                : p)
            .ToArray();
        return new FanCurve(clamped);
    }

    /// <summary>True when no hot point (>= HotTempThresholdC) is below the minimum
    /// safe duty — i.e. the curve will not let the GPU cook silently.</summary>
    public static bool IsFanCurveSafe(FanCurve curve) =>
        curve.Points.All(p => p.TempC < HotTempThresholdC || p.DutyPct >= MinFanDutyAtHotTempPct)
        // 24/09/2026 : les points chauds seuls ne suffisaient pas. (79 °C, 10 %) puis (100 °C, 40 %)
        // passait, et à 85 °C la carte tournait à 10-17 % selon que le pilote tient ou interpole ;
        // (60 °C, 20 %) seul passait aussi, et tenait 20 % à 90 °C. Le point qui gouverne 80 °C
        // (le dernier à 80 °C ou moins) doit lui aussi tenir le plancher.
        && (curve.Points.Where(p => p.TempC <= HotTempThresholdC).MaxBy(p => p.TempC) is not { } g
            || g.DutyPct >= MinFanDutyAtHotTempPct);
}

/// <summary>One point of a GPU fan curve: at TempC degrees, run DutyPct % duty.</summary>
public sealed record FanCurvePoint(int TempC, int DutyPct);

/// <summary>A GPU fan curve as an ordered list of (temp, duty) points.</summary>
public sealed record FanCurve(IReadOnlyList<FanCurvePoint> Points)
{
    /// <summary>The lowest duty assigned to any point at or above the hot-temperature
    /// threshold (0 when the curve has no hot points — there is nothing to overheat).</summary>
    public int MinDutyAtHotTemp
    {
        get
        {
            var hot = Points.Where(p => p.TempC >= GpuSafeBounds.HotTempThresholdC).ToArray();
            return hot.Length == 0 ? 0 : hot.Min(p => p.DutyPct);
        }
    }
}
