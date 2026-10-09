using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Profiles;

// -----------------------------------------------------------------------
// M6 — ChipClassifier: pure, conservative chip-family classification from the M1
// HardwareInventory. This is the "detected chip → which families apply" core of the
// matcher, kept side-effect-free so it is exhaustively unit-testable without DI.
//
// CONSERVATIVE + FAIL-CLOSED (cahier §10/§16): a family is
// claimed only when the inventory is confidently a member. An unrecognized vendor /
// blank or odd model / a laptop / an iGPU → NO chip-specific family (the matcher then
// returns only the safe generic baseline). We never guess a family from a partial
// string. Conventions mirror the existing detection layer (CpuDetector CCD heuristic,
// GpuGating discrete-desktop rule, GpuArchitectureGate name classifier) so M6 agrees
// with where the M2 tweaks actually apply.
// -----------------------------------------------------------------------
public static class ChipClassifier
{
    // A discrete GPU has dedicated VRAM; integrated GPUs report ~0 (shared system RAM).
    // Same 1 GiB floor the M2 GpuGating uses to call a GPU "discrete".
    private const long DiscreteVramFloorBytes = 1L << 30;

    // GeForce gaming marketing names only (RTX 20xx-50xx, GTX 10xx/16xx). Deliberately
    // does not match Quadro / Tesla / MX / unknown names → we never guess Option-A
    // eligibility on a non-GeForce or garbled name.
    private static readonly Regex GeForceModel =
        new(@"\b(RTX\s*\d{3,4}|GTX\s*1[06]\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for an AMD vendor string (CPU or GPU). Mirrors the detection layer:
    /// WMI reports "AuthenticAMD" for CPUs and "Advanced Micro Devices"/"ATI" for GPUs.</summary>
    public static bool IsAmd(string? vendor) =>
        !string.IsNullOrWhiteSpace(vendor) &&
        (vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)
         || vendor.Contains("Authentic", StringComparison.OrdinalIgnoreCase)
         || vendor.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
         || vendor.Contains("ATI", StringComparison.OrdinalIgnoreCase));

    /// <summary>True for an NVIDIA vendor string.</summary>
    public static bool IsNvidia(string? vendor) =>
        !string.IsNullOrWhiteSpace(vendor) &&
        vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    /// <summary>True only for a DUAL-CCD AMD X3D CPU (7950X3D / 7900X3D / 9950X3D /
    /// 9900X3D) — the chips the CCD-routing fix targets. Single-CCD X3D (7800X3D /
    /// 9800X3D, CcdCount &lt; 2) and Intel are excluded, exactly as the M2
    /// X3dCcdParkingFixTweak / research scope require ("ne touche pas les 7800X3D /
    /// 9800X3D ni Intel").</summary>
    public static bool IsAmdX3dDualCcd(CpuInfo? cpu) =>
        cpu is not null && IsAmd(cpu.Vendor) && cpu.IsX3D && cpu.CcdCount >= 2;

    /// <summary>True when this specific GPU is a tunable discrete desktop GeForce part:
    /// NVIDIA vendor, dedicated VRAM, a recognized GeForce name, and not a mobile / Max-Q
    /// part. (Chassis is checked by <see cref="HasTunableNvidiaDesktopGpu"/>.)</summary>
    public static bool IsTunableNvidiaGpu(GpuInfo? gpu) =>
        gpu is not null
        && IsNvidia(gpu.Vendor)
        && gpu.VramBytes >= DiscreteVramFloorBytes
        && !IsMobile(gpu.Model)
        && GeForceModel.IsMatch(gpu.Model ?? string.Empty);

    /// <summary>True when this machine is a DESKTOP carrying at least one tunable discrete
    /// GeForce GPU — where the NVIDIA Option-A GPU-tuning tweaks actually apply (GpuGating:
    /// desktop dGPU only; laptop / Optimus / iGPU / handheld are refused).</summary>
    public static bool HasTunableNvidiaDesktopGpu(HardwareInventory? hw) =>
        hw is not null
        && hw.Chassis == ChassisType.Desktop
        && hw.Gpus is not null
        && hw.Gpus.Any(IsTunableNvidiaGpu);

    // Mobile GPUs are driver-locked (no user clock/power/fan control) — guided in M3.
    private static bool IsMobile(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        (model.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
         || model.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
         || model.Contains("Max-Q", StringComparison.OrdinalIgnoreCase));
}
