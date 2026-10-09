using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxGpuTuningReader — implements IAdlxGpuTuning over IAdlxNative.
//
// Pure domain logic: maps the boundary's raw reads to domain types and
// Result error codes. Contains NO native calls — fully testable against
// FakeAdlxNative without AMD hardware.
// -----------------------------------------------------------------------
public sealed class AdlxGpuTuningReader : IAdlxGpuTuning
{
    private readonly IAdlxNative _native;

    public AdlxGpuTuningReader(IAdlxNative native) => _native = native;

    public bool IsAvailable => _native.IsAvailable;

    public Result<int> ResolveTunableGpuIndex()
    {
        // Ported from the AMD harness picker: on an iGPU + dGPU box ADLX index 0 is often
        // the non-tunable iGPU, so probe a small range and pick the first GPU whose GFX
        // tuning reads cleanly — the discrete AMD card. Fail-clean if none is tunable.
        if (!_native.IsAvailable)
            return Fail("ADLX SDK not available (AMD driver / GPU absent).");

        for (int i = 0; i < AdlxGpuProbe.MaxProbeIndices; i++)
        {
            if (GetUndervoltValue(i).IsOk)
                return Result<int>.Ok(i);
        }

        return Fail("No tunable AMD GPU found (ADLX GFX tuning did not read on any GPU index).");
    }

    public Result<int> GetUndervoltValue(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Fail($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Fail("ADLX SDK not available (AMD driver / GPU absent).");
        return _native.TryReadGfx(gpuIndex, out var r)
            ? Result<int>.Ok(r.VoltMv)
            : Fail("ADLX graphics tuning read failed (GPU not supported or interface absent).");
    }

    public Result<int> GetGfxMaxFreqMhz(int gpuIndex)
    {
        // The absolute current GFX max clock (MHz) — the value the undervolt write sets.
        // The undervolt tweak reads this to build an absolute target (current + offset) and
        // to capture the absolute original for a faithful revert (mirrors the power-limit %
        // round-trip). Fail-clean on a bad index / unavailable SDK / read failure.
        if (gpuIndex < 0)
            return Fail($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Fail("ADLX SDK not available (AMD driver / GPU absent).");
        return _native.TryReadGfx(gpuIndex, out var r)
            ? Result<int>.Ok(r.MaxFreqMhz)
            : Fail("ADLX graphics tuning read failed (GPU not supported or interface absent).");
    }

    public Result<int> GetPowerLimitPct(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Fail($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Fail("ADLX SDK not available (AMD driver / GPU absent).");
        return _native.TryReadPower(gpuIndex, out var r)
            ? Result<int>.Ok(r.PowerLimitPct)
            : Fail("ADLX power-limit read failed (GPU not supported or interface absent).");
    }

    public Result<FanCurve> GetFanCurve(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable,
                "ADLX SDK not available (AMD driver / GPU absent).");
        if (!_native.TryReadFan(gpuIndex, out var r))
            return Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable,
                "ADLX fan-tuning read failed (GPU not supported or interface absent).");

        IReadOnlyList<AdlxFanPoint> src = r.Points ?? Array.Empty<AdlxFanPoint>();
        var pts = new FanCurvePoint[src.Count];
        for (int i = 0; i < src.Count; i++)
            pts[i] = new FanCurvePoint(src[i].TempC, src[i].SpeedPct);

        return Result<FanCurve>.Ok(new FanCurve(pts));
    }

    private static Result<int> Fail(string msg) =>
        Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, msg);
}
