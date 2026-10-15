using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Profiles;

// -----------------------------------------------------------------------
// ChipClassifier : classement pur et prudent de la famille de puce à partir de l'inventaire
// du scan. C'est le cœur « puce détectée → familles concernées » du matcher, sans effet de
// bord pour être testable unitairement sans injection.
//
// Prudent et fermé par défaut : une famille n'est revendiquée que si l'inventaire en est un
// membre certain. Constructeur inconnu, modèle vide ou étrange, portable, iGPU → aucune
// famille propre à la puce (le matcher ne rend alors que la base générique sûre). Jamais de
// famille devinée depuis une chaîne partielle. Les conventions suivent la couche de
// détection (heuristique CCD de CpuDetector, règle « dGPU de bureau » de GpuGating,
// classement par nom de GpuArchitectureGate) pour que les profils s'accordent avec là où
// les leviers s'appliquent vraiment.
// -----------------------------------------------------------------------
public static class ChipClassifier
{
    // Une carte dédiée a sa propre VRAM ; une carte intégrée en déclare ~0 (mémoire partagée).
    // Même plancher de 1 Gio que GpuGating pour dire « dédiée ».
    private const long DiscreteVramFloorBytes = 1L << 30;

    // Noms commerciaux GeForce seulement (RTX 20xx-50xx, GTX 10xx/16xx). Volontairement sans
    // Quadro, Tesla, MX ni nom inconnu : l'éligibilité à l'option A n'est jamais devinée sur
    // un nom hors GeForce ou abîmé.
    private static readonly Regex GeForceModel =
        new(@"\b(RTX\s*\d{3,4}|GTX\s*1[06]\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Vrai pour une chaîne constructeur AMD (processeur ou carte). Même lecture que la
    /// détection : WMI dit « AuthenticAMD » pour les processeurs, « Advanced Micro Devices » ou « ATI » pour les cartes.</summary>
    public static bool IsAmd(string? vendor) =>
        !string.IsNullOrWhiteSpace(vendor) &&
        (vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)
         || vendor.Contains("Authentic", StringComparison.OrdinalIgnoreCase)
         || vendor.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
         || vendor.Contains("ATI", StringComparison.OrdinalIgnoreCase));

    /// <summary>Vrai pour une chaîne constructeur NVIDIA.</summary>
    public static bool IsNvidia(string? vendor) =>
        !string.IsNullOrWhiteSpace(vendor) &&
        vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    /// <summary>Vrai seulement pour un X3D AMD à deux CCD (7950X3D, 7900X3D, 9950X3D, 9900X3D) :
    /// les puces visées par la correction du routage CCD. Les X3D à un seul CCD (7800X3D,
    /// 9800X3D, CcdCount &lt; 2) et Intel sont exclus, comme l'exigent X3dCcdParkingFixTweak et
    /// le périmètre de la recherche (« ne touche pas les 7800X3D / 9800X3D ni Intel »).</summary>
    public static bool IsAmdX3dDualCcd(CpuInfo? cpu) =>
        cpu is not null && IsAmd(cpu.Vendor) && cpu.IsX3D && cpu.CcdCount >= 2;

    /// <summary>Vrai quand cette carte est une GeForce de bureau réglable : constructeur NVIDIA,
    /// VRAM dédiée, nom GeForce reconnu, ni portable ni Max-Q. (Le châssis est vérifié par
    /// <see cref="HasTunableNvidiaDesktopGpu"/>.)</summary>
    public static bool IsTunableNvidiaGpu(GpuInfo? gpu) =>
        gpu is not null
        && IsNvidia(gpu.Vendor)
        && gpu.VramBytes >= DiscreteVramFloorBytes
        && !IsMobile(gpu.Model)
        && GeForceModel.IsMatch(gpu.Model ?? string.Empty);

    /// <summary>Vrai quand la machine est un poste de bureau avec au moins une GeForce dédiée
    /// réglable : là où les leviers NVIDIA de l'option A s'appliquent vraiment (GpuGating :
    /// dGPU de bureau seulement ; portable, Optimus, iGPU et console portable refusés).</summary>
    public static bool HasTunableNvidiaDesktopGpu(HardwareInventory? hw) =>
        hw is not null
        && hw.Chassis == ChassisType.Desktop
        && hw.Gpus is not null
        && hw.Gpus.Any(IsTunableNvidiaGpu);

    // Les cartes mobiles sont verrouillées par le pilote (ni fréquence, ni puissance, ni ventilateur) : guidées vers le BIOS.
    private static bool IsMobile(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        (model.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
         || model.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
         || model.Contains("Max-Q", StringComparison.OrdinalIgnoreCase));
}
