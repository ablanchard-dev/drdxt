using DrDxt.Core;
using DrDxt.GpuNative.Nvml;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Lit l'état du Resizable BAR NVIDIA par la taille BAR1 de NVML (la source de nvidia-smi).
/// Rend Unknown si NVML manque, s'il n'y a pas de GPU NVIDIA ou si la lecture échoue : jamais
/// Off à l'aveugle (étiquette honnête).
/// </summary>
public sealed class NvmlReBarProvider : INvReBarProvider
{
    private readonly INvmlNative _nvml;

    public NvmlReBarProvider(INvmlNative nvml) => _nvml = nvml;

    public TriState GetReBarState()
    {
        if (!_nvml.IsAvailable) return TriState.Unknown;

        // Device 0. GpuDetector ne l'appelle que s'il n'y a qu'une carte NVIDIA ; avec plusieurs,
        // il rend Unknown (appariement DXGI <-> NVML par bus PCI non fait).
        return _nvml.TryGetBar1AndFbBytes(0, out var bar1, out var fb)
            ? ReBarHeuristic.FromBar1AndFb(bar1, fb)
            : TriState.Unknown;
    }
}
