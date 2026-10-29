using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// Conditions matérielles des leviers de latence, prédicats purs testés à part. « GPU de rendu » = un GPU dédié qui rend
// le jeu, ni intégré (Intel UHD/Iris, iGPU des APU AMD) ni mobile verrouillé de portable.
// -----------------------------------------------------------------------
internal static class LatencyGating
{
    /// <summary>Vrai si un GPU de rendu NVIDIA dédié est présent (de bureau, pas un « Laptop GPU »
    /// mobile). La condition de châssis revient à l'appelant.</summary>
    public static bool HasDesktopNvidiaRenderGpu(HardwareInventory hw) =>
        hw.Gpus.Any(g => IsNvidia(g) && IsDiscreteRenderGpu(g) && !IsMobileGpu(g));

    /// <summary>Les réglages du pilote (file de rendu, plafond sous VRR) sont réversibles et marchent
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

    public static bool IsAmd(GpuInfo g) => CartesGraphiques.EstAmd(g.Vendor);

    // La règle unique du produit (mémoire et nom) : une APU à grosse réserve UMA n'est pas une carte de rendu dédiée.
    private static bool IsDiscreteRenderGpu(GpuInfo g) => CartesGraphiques.EstDediee(g);

    // GPU mobiles : horloges verrouillées par le pilote. Les réglages du pilote marchent quand même (voir HasNvidiaDriverPath).
    private static bool IsMobileGpu(GpuInfo g) =>
        g.Model.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
        || g.Model.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
        || g.Model.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);
}
