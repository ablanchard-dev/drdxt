using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// Shared hardware gating for the latency tweaks (master plan §16). Pure,
// side-effect-free predicates so D1 (NVIDIA Reflex floor) and D2 (universal
// render-queue cap) can gate complementarily and be unit-tested in isolation.
//
// "Render GPU" = a discrete GPU that actually renders the game. We exclude
// integrated GPUs (Intel UHD/Iris, AMD APU iGPUs) and laptop-locked mobile GPUs.
// -----------------------------------------------------------------------
internal static class LatencyGating
{
    /// <summary>True if a NVIDIA discrete render GPU is present (desktop-class,
    /// not a mobile "Laptop GPU"). Chassis gating is the caller's responsibility.</summary>
    public static bool HasDesktopNvidiaRenderGpu(HardwareInventory hw) =>
        hw.Gpus.Any(g => IsNvidia(g) && IsDiscreteRenderGpu(g) && !IsMobileGpu(g));

    /// <summary>True if a AMD discrete render GPU is present (and no NVIDIA render
    /// GPU is taking the Reflex path). Used by D2's complementary AMD branch.</summary>
    public static bool HasAmdRenderGpu(HardwareInventory hw) =>
        hw.Gpus.Any(g => IsAmd(g) && IsDiscreteRenderGpu(g) && !IsMobileGpu(g));

    /// <summary>LAP1 (08/10/2026) : les réglages du pilote (file de rendu, plafond sous VRR) sont réversibles et marchent
    /// aussi sur une carte de portable : seules ses fréquences sont verrouillées. Fixe ou portable, jamais console.</summary>
    public static bool FixeOuPortable(HardwareInventory hw) => hw.Chassis is ChassisType.Desktop or ChassisType.Laptop;

    /// <summary>Une carte NVIDIA dédiée qui rend le jeu ; sur un portable, la carte mobile compte.</summary>
    public static bool HasNvidiaDriverPath(HardwareInventory hw) =>
        hw.Gpus.Any(g => IsNvidia(g) && IsDiscreteRenderGpu(g) && (!IsMobileGpu(g) || hw.Chassis == ChassisType.Laptop));

    /// <summary>Une carte AMD dédiée qui rend le jeu ; sur un portable, la carte mobile compte.</summary>
    public static bool HasAmdDriverPath(HardwareInventory hw) =>
        hw.Gpus.Any(g => IsAmd(g) && IsDiscreteRenderGpu(g) && (!IsMobileGpu(g) || hw.Chassis == ChassisType.Laptop));

    public static bool IsNvidia(GpuInfo g) =>
        g.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    public static bool IsAmd(GpuInfo g) =>
        g.Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)
        || g.Vendor.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
        || g.Vendor.Contains("ATI", StringComparison.OrdinalIgnoreCase);

    // A discrete render GPU has dedicated VRAM. Integrated GPUs report ~0 dedicated
    // VRAM (they share system memory) — we treat anything below 1 GiB as integrated.
    private static bool IsDiscreteRenderGpu(GpuInfo g) =>
        !IsIntel(g) && g.VramBytes >= (1L << 30);

    private static bool IsIntel(GpuInfo g) =>
        g.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    // Mobile GPUs: clocks are driver-locked. Driver settings still work (see HasNvidiaDriverPath).
    private static bool IsMobileGpu(GpuInfo g) =>
        g.Model.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
        || g.Model.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
        || g.Model.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);
}
