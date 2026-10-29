using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;

namespace DrDxt.Detection.Gpu.Nvapi;

// NvApiGpuTuningReader : INvApiGpuTuning sur INvApiNative, le pendant NVIDIA d'AdlxGpuTuningReader. Traduit les lectures
// de l'unique frontière NVAPI en Result du domaine, sans appel natif (testé contre FakeNvApiNative, nvapi64.dll jamais
// chargé en test) ni élévation (NvAPI_Initialize et les GET n'en demandent pas ; l'écriture passe par IElevatedClient).
//   • Décalages cœur / mémoire et limite de puissance : vraies lectures sur le GET vérifié. GpuUndervoltTweak.ApplyNvidia
//     relève les deux décalages pour un retour fidèle et échoue si l'une manque : un placebo la casserait.
//   • Courbe de ventilateur : échec propre, jamais une courbe inventée. Sa lecture n'est pas dans la recette vérifiée
//     (GetPstates20 + ClientPowerPolicies seulement), et ApplyNvidia ne la lit pas.
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
        // Hors de la recette vérifiée : échec propre, jamais une courbe inventée ou vide.
        Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable,
            "NVAPI fan-curve read is not implemented (not in the verified tuning recipe; fan tweak deferred).");

    private static Result<int> Fail(string msg) =>
        Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, msg);
}
