using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// M6 — IGpuTuningTargetSource: the live GPU context the GPU tweaks read at APPLY time.
//
// In M2 the targets were injected as immutable value records (pure, unit-testable). M6
// makes them OVERRIDABLE per scan: a per-chip profile (DrDxt.Profiles) supplies the
// values, and the scan flow Set()s them here after clamping each one to GpuSafeBounds.
//
// The source is a HOLDER, not an authorization: every GPU tweak still re-clamps/gates
// the value via GpuSafeBounds before any privileged write (defense in depth — "a profile
// is a request, not an authorization", mirroring GpuTuningTarget's own contract).
//
// It also carries the HARDWARE fact "is an AMD GPU present" (computed from the scanned
// HardwareInventory via GpuGating.IsAmd — DXGI vendor 0x1002), set at scan time from the
// Same hw that drives the targets. This fact is always knowable, INDEPENDENT of whether
// the ADLX tuning SDK loaded — so a GPU tweak can surface an honest "AMD GPU detected —
// tuning coming soon" (§22.3) instead of a silent skip when ADLX is unavailable. It must
// live here, not on the ADLX seam, whose prod impl reports unavailable on every machine.
//
// Seeded with the same conservative, in-bounds defaults M2 used, so behaviour with NO
// profile override is byte-identical to before. Reset() returns to those defaults (and
// clears the presence flag) so a previous scan's override never leaks into the next.
// -----------------------------------------------------------------------
public interface IGpuTuningTargetSource
{
    /// <summary>The current core/memory clock-offset target the undervolt tweak applies.</summary>
    GpuTuningTarget Tuning { get; }

    /// <summary>The current power-limit target the power-limit tweak applies.</summary>
    GpuPowerLimitTarget PowerLimit { get; }

    /// <summary>True when an AMD GPU was detected in the scanned hardware, INDEPENDENT of
    /// whether the ADLX tuning SDK is available. A hardware fact (always knowable), used so
    /// the GPU tweaks surface the honest "AMD GPU detected — tuning coming soon" status
    /// instead of a silent skip when ADLX is unavailable. False until set from hardware.</summary>
    bool IsAmdGpuPresent { get; }

    /// <summary>Overrides the live targets (caller must have clamped to GpuSafeBounds).</summary>
    void Set(GpuTuningTarget tuning, GpuPowerLimitTarget powerLimit);

    /// <summary>Records whether an AMD GPU is present, computed internally from the scanned
    /// hardware (hw.Gpus via GpuGating.IsAmd). Called at scan time, before any GPU tweak
    /// applies. A null / empty inventory → not present (fail-closed).</summary>
    void SetAmdGpuPresenceFromHardware(HardwareInventory hw);

    /// <summary>Restores the conservative seeded defaults and clears the presence flag
    /// (no profile override active, no stale hardware fact).</summary>
    void Reset();
}

public sealed class GpuTuningTargetSource : IGpuTuningTargetSource
{
    private readonly GpuTuningTarget _defaultTuning;
    private readonly GpuPowerLimitTarget _defaultPowerLimit;

    public GpuTuningTargetSource(GpuTuningTarget defaultTuning, GpuPowerLimitTarget defaultPowerLimit)
    {
        _defaultTuning = defaultTuning ?? throw new ArgumentNullException(nameof(defaultTuning));
        _defaultPowerLimit = defaultPowerLimit ?? throw new ArgumentNullException(nameof(defaultPowerLimit));
        Tuning = _defaultTuning;
        PowerLimit = _defaultPowerLimit;
    }

    public GpuTuningTarget Tuning { get; private set; }

    public GpuPowerLimitTarget PowerLimit { get; private set; }

    public bool IsAmdGpuPresent { get; private set; }

    public void Set(GpuTuningTarget tuning, GpuPowerLimitTarget powerLimit)
    {
        Tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        PowerLimit = powerLimit ?? throw new ArgumentNullException(nameof(powerLimit));
    }

    public void SetAmdGpuPresenceFromHardware(HardwareInventory hw) =>
        IsAmdGpuPresent = hw?.Gpus is { } gpus && gpus.Any(GpuGating.IsAmd);

    public void Reset()
    {
        Tuning = _defaultTuning;
        PowerLimit = _defaultPowerLimit;
        IsAmdGpuPresent = false;
    }
}
