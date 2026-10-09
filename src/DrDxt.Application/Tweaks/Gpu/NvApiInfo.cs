using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// NvApiInfo — the real (read-only, unelevated-safe) INvApiInfo.
//
// Reads each NVIDIA GPU's identity (marketing name + coarse architecture) so a
// later voltage-undervolt gate can disable direct voltage writes on Blackwell
// (RTX 50) — recipe "Blackwell / RTX 50 lockout".
//
// Source of truth: the GPU NAME already detected by the M1 hardware detector
// (WMI / DXGI), which is reliable and needs NO elevation and NO NVAPI struct
// marshalling. Architecture is derived from the name via GpuArchitectureGate (one
// shared classifier). This avoids shipping an UNVERIFIED NVAPI function id
// (NvAPI_GPU_GetArchInfo / GetFullName are not in the verified recipe) while still
// delivering a real name + Blackwell-vs-Ada read.
//
// FAIL-CLEAN: no NVIDIA GPU detected → IsAvailable is false and GetGpuIdentity
// fails clean (TWEAK.SDK_UNAVAILABLE). Never throws.
// -----------------------------------------------------------------------
public sealed class NvApiInfo : INvApiInfo
{
    private readonly IReadOnlyList<GpuInfo> _nvidiaGpus;

    /// <summary>Builds from the detected hardware inventory's GPU list. Only NVIDIA
    /// GPUs are retained (the seam is the NVIDIA identity reader).</summary>
    public NvApiInfo(IReadOnlyList<GpuInfo> gpus) =>
        _nvidiaGpus = (gpus ?? Array.Empty<GpuInfo>())
            .Where(g => g.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public bool IsAvailable => _nvidiaGpus.Count > 0;

    public Result<NvGpuIdentity> GetGpuIdentity(int gpuIndex)
    {
        if (gpuIndex < 0 || gpuIndex >= _nvidiaGpus.Count)
            return Result<NvGpuIdentity>.Fail(ErrorCodes.TweakSdkUnavailable,
                "No NVIDIA GPU at the requested index (NVAPI unavailable).");

        var gpu = _nvidiaGpus[gpuIndex];
        var arch = GpuArchitectureGate.ClassifyByName(gpu.Model);
        return Result<NvGpuIdentity>.Ok(new NvGpuIdentity(gpu.Model, arch));
    }
}
