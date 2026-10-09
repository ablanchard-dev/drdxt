namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuTuningTarget — the desired GPU tuning values a tweak should apply.
//
// Under Option A (master §11) an undervolt is a small NEGATIVE core-clock offset
// (MHz). These values are still subject to GpuSafeBounds clamping/gating before
// anything reaches the helper — the target is a request, not an authorization.
//
// In production these come from a per-chip profile (M6); for M2 they are injected
// so the tweak stays pure and unit-testable.
// -----------------------------------------------------------------------
public sealed record GpuTuningTarget(int GpuIndex, int CoreOffsetMhz, int MemoryOffsetMhz);

/// <summary>The desired power-limit (% of TDP) a power-limit tweak should apply.</summary>
public sealed record GpuPowerLimitTarget(int GpuIndex, int PowerLimitPct);

/// <summary>The desired fan curve a fan-curve tweak should apply.</summary>
public sealed record GpuFanCurveTarget(int GpuIndex, FanCurve Curve);
