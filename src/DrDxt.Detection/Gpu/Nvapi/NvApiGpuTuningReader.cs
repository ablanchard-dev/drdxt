using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.GpuNative.NvApi;

namespace DrDxt.Detection.Gpu.Nvapi;

// -----------------------------------------------------------------------
// NvApiGpuTuningReader : implémente INvApiGpuTuning sur INvApiNative.
//
// Le pendant NVIDIA d'AdlxGpuTuningReader : pure logique de domaine qui traduit les lectures
// brutes de l'unique frontière native NVAPI en Result du domaine. Aucun appel natif ici :
// entièrement testable contre FakeNvApiNative sans GPU NVIDIA (nvapi64.dll n'est jamais
// chargé dans un processus de test). Sans élévation : NvAPI_Initialize et les GET marchent sans
// élévation ; les écritures privilégiées passent par les ops typées d'IElevatedClient, pas ici.
//
//   • Décalage d'horloge cœur / mémoire + limite de puissance = vraies lectures sur le GET
//     vérifié. La lecture mémoire est réelle exprès : la pose de sous-tension
//     (GpuUndervoltTweak.ApplyNvidia) relève les décalages cœur et mémoire pour un retour fidèle
//     et fait échouer toute la sous-tension si l'une des lectures échoue ; un placebo ici la casserait.
//   • Courbe de ventilateur = échec propre et honnête. La lecture NVAPI de la courbe n'est pas
//     dans la recette de réglage vérifiée (GetPstates20 + ClientPowerPolicies seulement) et le
//     levier ventilateur attend pour les deux fabricants ; ApplyNvidia ne lit jamais la courbe. On
//     refuse honnêtement plutôt que d'inventer une courbe vide ou fausse : jamais un chiffre placebo.
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
        // Indisponible, honnêtement : la lecture NVAPI de la courbe n'est pas dans la recette vérifiée
        // et le levier ventilateur attend → échec propre, jamais une courbe inventée ou vide.
        Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable,
            "NVAPI fan-curve read is not implemented (not in the verified tuning recipe; fan tweak deferred).");

    private static Result<int> Fail(string msg) =>
        Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, msg);
}
