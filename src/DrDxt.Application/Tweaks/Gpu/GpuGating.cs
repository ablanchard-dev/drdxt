using System.Text.RegularExpressions;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// Conditions matérielles strictes des leviers GPU (sous-tension, limite de puissance, ventilateur) : cartes dédiées de
// PC de bureau seulement. Refusé sur :
//   * les portables / GPU mobiles Optimus (verrouillés par le pilote, guidés par le BIOS),
//   * les GPU intégrés (Intel UHD/Iris, iGPU des APU AMD : rien d'utile à régler),
//   * les consoles portables (profils de puissance verrouillés par le firmware).
// Prédicats purs, la même condition pour chaque levier, testée à part.
// -----------------------------------------------------------------------
internal static class GpuGating
{
    /// <summary>Vrai seulement sur un PC de bureau qui a un GPU de rendu NVIDIA ou AMD dédié
    /// (ni mobile, ni intégré). Faux sur portable / Optimus / iGPU / console portable.</summary>
    public static bool HasDesktopDiscreteGpu(HardwareInventory hw)
    {
        if (hw.Chassis != ChassisType.Desktop)
            return false;
        return hw.Gpus.Any(IsTunableDiscreteGpu);
    }

    /// <summary>Vrai si ce GPU est un GPU NVIDIA ou AMD dédié et réglable
    /// (VRAM dédiée, ni intégré, ni mobile / Max-Q).</summary>
    public static bool IsTunableDiscreteGpu(GpuInfo g) =>
        (IsNvidia(g) || IsAmd(g)) && IsDiscrete(g) && !IsMobileGpu(g);

    public static bool IsNvidia(GpuInfo g) =>
        g.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    public static bool IsAmd(GpuInfo g) => CartesGraphiques.EstAmd(g.Vendor);

    // La règle unique du produit (mémoire et nom) : une APU à grosse réserve UMA n'est pas une carte dédiée.
    private static bool IsDiscrete(GpuInfo g) => CartesGraphiques.EstDediee(g);

    // Les GPU mobiles sont verrouillés par le pilote (pas de contrôle horloge / puissance / ventilateur par ces chemins).
    private static bool IsMobileGpu(GpuInfo g) =>
        g.Model.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
        || g.Model.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
        || g.Model.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);

    /// <summary>Vrai quand l'architecture de ce GPU permet l'écriture directe de sous-tension (voir
    /// GpuArchitectureGate.SupportsVoltageUndervolt(GpuInfo), dont c'est le relais). Sur Blackwell (RTX 50) la voie de
    /// tension est verrouillée : une garde de sous-tension y passe par limite de puissance + décalage cœur.</summary>
    public static bool SupportsVoltageUndervolt(GpuInfo g) =>
        GpuArchitectureGate.SupportsVoltageUndervolt(g);
}

// -----------------------------------------------------------------------
// GpuArchitectureGate : classificateur pur des architectures et garde de tension, une seule source pour la recette
// « Blackwell / RTX 50 lockout », testable sans NVAPI. Par le nom commercial (« RTX 5080 » → Blackwell, « RTX 4070 » →
// Ada), sûr sans élévation et indépendant du pilote (GpuInfo.Model). NvAPI_GPU_GetArchInfo donnerait l'id du silicium,
// mais son id de fonction n'est pas vérifié dans la recette.
// -----------------------------------------------------------------------
public static class GpuArchitectureGate
{
    // « RTX 50xx » (toute série 50, modèle à 4 chiffres commençant par 50) = Blackwell.
    private static readonly Regex BlackwellModel =
        new(@"\bRTX\s*50\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // « RTX 40xx/30xx/20xx », « GTX 16xx/10xx » = Ada ou plus ancien (ancienne écriture de tension permise).
    private static readonly Regex AdaOrOlderModel =
        new(@"\b(RTX\s*(40|30|20)\d{2}|GTX\s*(16|10)\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // « RX 7xxx » = RDNA 3, « RX 9xxx » = RDNA 4 : le nom, lisible sans elevation ni pilote, comme la matrice de
    // couverture l'exige.
    private static readonly Regex Rdna3Model =
        new(@"\bRX\s*7\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Rdna2Model =
        new(@"\bRX\s*6\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Rdna4Model =
        new(@"\bRX\s*9\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // « RX 5xxx » (quatre chiffres) = RDNA 1. « RX 4x0/5x0 » (trois chiffres, suffixe X ou D possible), « Vega » et
    // « Radeon VII » = GCN. Le nombre de chiffres sépare une RX 5700 d'une RX 570.
    private static readonly Regex Rdna1Model =
        new(@"\bRX\s*5\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GcnModel =
        new(@"\bRX\s*[45]\d0[XD]?\b|\bVega\b|\bRadeon\s+VII\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Classe le nom commercial d'un GPU NVIDIA ou Radeon (GCN, RDNA 1 à 4) en architecture grossière.
    /// Nom inconnu ou d'un autre fabricant → Unknown (les gardes le traitent avec prudence).</summary>
    public static GpuArchitecture ClassifyByName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return GpuArchitecture.Unknown;
        if (BlackwellModel.IsMatch(model))
            return GpuArchitecture.Blackwell;
        if (AdaOrOlderModel.IsMatch(model))
            return GpuArchitecture.AdaOrOlder;
        if (Rdna4Model.IsMatch(model))
            return GpuArchitecture.Rdna4;
        if (Rdna3Model.IsMatch(model))
            return GpuArchitecture.Rdna3;
        if (Rdna2Model.IsMatch(model))
            return GpuArchitecture.Rdna2;
        if (Rdna1Model.IsMatch(model))
            return GpuArchitecture.Rdna1;
        if (GcnModel.IsMatch(model))
            return GpuArchitecture.Gcn;
        return GpuArchitecture.Unknown;
    }

    /// <summary>Cette architecture accepte-t-elle l'écriture directe de sous-tension ? Blackwell verrouillé, Ada ou plus
    /// ancien permis, Unknown non pris en charge.</summary>
    public static bool SupportsVoltageUndervolt(GpuArchitecture architecture) => architecture switch
    {
        // L'architecture garde le voltage. Ce n'est pas une validation : le rapport archive (GpuTuningCoverage.ValideesEnVie,
        // seule Ada en a un) ne sert qu'a la couverture affichee ; aucun levier ne le consulte avant d'ecrire.
        GpuArchitecture.AdaOrOlder => true,
        GpuArchitecture.Rdna4 => true,
        // Blackwell : NVIDIA a verrouille le chemin voltage.
        GpuArchitecture.Blackwell => false,
        // RDNA3 garde le voltage manuel mais n'a jamais ete testee en ecriture (« fail-closed materiel »).
        GpuArchitecture.Rdna3 => false,
        // Une architecture qu'on ne sait pas nommer n'est pas une architecture validee.
        _ => false,
    };

    /// <summary>Ce GPU permet-il l'écriture directe de sous-tension ? NVIDIA : oui sauf Blackwell (RTX 50, limite de
    /// puissance + décalage cœur). AMD : non pour GCN et RDNA 1 à 3 (jamais testées), oui pour les autres, même non classées
    /// (leur chemin ADLX) : sur un AMD non classé, la réponse diffère donc de celle par architecture.</summary>
    public static bool SupportsVoltageUndervolt(GpuInfo gpu)
    {
        var architecture = ClassifyByName(gpu.Model);

        // « Le verrou Blackwell ne concerne pas AMD » laisserait passer RDNA3 (voltage manuel, jamais testée), et RDNA2,
        // RDNA1, GCN, jamais testées non plus.
        if (architecture is GpuArchitecture.Rdna3 or GpuArchitecture.Rdna2 or GpuArchitecture.Rdna1 or GpuArchitecture.Gcn)
            return false;

        var isNvidia = gpu.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        if (!isNvidia)
            // Les autres cartes AMD gardent leur chemin ADLX : refuser les non classees couperait sur une supposition ; la
            // matrice de couverture dit ce qu'on ignore.
            return true;

        return architecture != GpuArchitecture.Blackwell;
    }
}
