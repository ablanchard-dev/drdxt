using DrDxt.Core;
using DrDxt.GpuNative.Nvml;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Reads NVIDIA Resizable BAR state via NVML BAR1 size (the source nvidia-smi uses).
/// Returns Unknown when NVML is absent, there is no NVIDIA GPU, or the read fails — never
/// claims Off blindly (§16 honest labelling).
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
