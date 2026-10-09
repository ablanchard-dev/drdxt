using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;

namespace DrDxt.Detection.Gpu.Nvapi;

// -----------------------------------------------------------------------
// NvApiGpuTuningReader — implements INvApiGpuTuning over INvApiNative.
//
// The NVIDIA mirror of AdlxGpuTuningReader: pure domain logic mapping the single
// NVAPI native boundary's raw reads to domain Result types. Contains NO native calls
// — fully testable against FakeNvApiNative without an NVIDIA GPU (nvapi64.dll is never
// loaded in a test process). Non-elevated: NvAPI_Initialize + GET work without
// elevation; the privileged WRITES go through IElevatedClient typed ops, not here.
//
//   • Core / memory clock offset + power limit = real reads off the verified GET. The
//     memory read is real on purpose: the undervolt apply (GpuUndervoltTweak.ApplyNvidia)
//     captures both the core AND memory offset for a faithful revert and fails the whole
//     undervolt if either read fails — a fail-clean placebo here would defeat it.
//   • Fan curve = HONEST fail-clean (§22.3). The NVAPI fan-curve read is not in the
//     verified tuning recipe (GetPstates20 + ClientPowerPolicies only) and the fan tweak
//     (E4) is deferred for both vendors; ApplyNvidia never reads the fan curve. We refuse
//     honestly rather than fabricate an empty/fake curve — never a placebo number.
// -----------------------------------------------------------------------
public sealed class NvApiGpuTuningReader : INvApiGpuTuning
{
    private readonly INvApiNative _native;

    public NvApiGpuTuningReader(INvApiNative native) => _native = native;

    public bool IsAvailable => _native.IsAvailable;

    public Result<int> GetCoreClockOffsetMhz(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Fail($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Fail("NVAPI not available (NVIDIA driver / GPU absent).");
        return _native.TryReadCoreOffsetMhz(gpuIndex, out var mhz)
            ? Result<int>.Ok(mhz)
            : Fail("NVAPI core-clock offset read failed (GPU not supported or driver fault).");
    }

    public Result<int> GetMemoryClockOffsetMhz(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Fail($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Fail("NVAPI not available (NVIDIA driver / GPU absent).");
        return _native.TryReadMemoryOffsetMhz(gpuIndex, out var mhz)
            ? Result<int>.Ok(mhz)
            : Fail("NVAPI memory-clock offset read failed (memory clock domain absent or driver fault).");
    }

    public Result<int> GetPowerLimitPct(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Fail($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Fail("NVAPI not available (NVIDIA driver / GPU absent).");
        return _native.TryReadPowerLimitPct(gpuIndex, out var pct)
            ? Result<int>.Ok(pct)
            : Fail("NVAPI power-limit read failed (GPU not supported or driver fault).");
    }

    public Result<FanCurve> GetFanCurve(int gpuIndex) =>
        // §22.3 honest-unavailable: NVAPI fan-curve read is not in the verified recipe and
        // the fan tweak is deferred → fail clean, never a fabricated/empty curve.
        Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable,
            "NVAPI fan-curve read is not implemented (not in the verified tuning recipe; fan tweak deferred).");

    private static Result<int> Fail(string msg) =>
        Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, msg);
}
