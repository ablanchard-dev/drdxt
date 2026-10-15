using System.Text.RegularExpressions;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// Conditions matérielles strictes, partagées par les leviers de réglage GPU.
//
// Le réglage GPU (sous-tension / limite de puissance / courbe de ventilateur) est réservé aux
// cartes dédiées de PC de bureau. Il est refusé sur :
//   * les portables / GPU mobiles Optimus (verrouillés par le pilote, guidés par le BIOS),
//   * les GPU intégrés (Intel UHD/Iris, iGPU des APU AMD : rien d'utile à régler),
//   * les consoles portables (profils de puissance verrouillés par le firmware).
//
// Prédicats purs, sans effet de bord : chaque levier GPU applique la même condition, testée
// à part.
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

    public static bool IsAmd(GpuInfo g) =>
        g.Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)
        || g.Vendor.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
        || g.Vendor.Contains("ATI", StringComparison.OrdinalIgnoreCase);

    private static bool IsIntel(GpuInfo g) =>
        g.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    // Un GPU dédié a de la VRAM dédiée. Les GPU intégrés en rapportent ~0 (ils partagent
    // la mémoire système) : tout ce qui est sous 1 Gio compte comme intégré.
    private static bool IsDiscrete(GpuInfo g) =>
        !IsIntel(g) && g.VramBytes >= (1L << 30);

    // Les GPU mobiles sont verrouillés par le pilote (pas de contrôle horloge / puissance / ventilateur par ces chemins).
    private static bool IsMobileGpu(GpuInfo g) =>
        g.Model.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
        || g.Model.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
        || g.Model.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);

    /// <summary>Vrai quand ce GPU NVIDIA accepte l'écriture directe de sous-tension.
    /// Sur Blackwell (RTX 50) la voie de tension est verrouillée (recette) : une garde de
    /// sous-tension doit y passer par limite de puissance + décalage cœur. Délègue au
    /// GpuArchitectureGate public (un seul classificateur partagé).</summary>
    public static bool SupportsVoltageUndervolt(GpuInfo g) =>
        GpuArchitectureGate.SupportsVoltageUndervolt(g);
}

// -----------------------------------------------------------------------
// GpuArchitectureGate : classificateur public et pur des architectures NVIDIA + garde de tension.
//
// Expose la détection Blackwell / RTX 50, pour qu'une garde de sous-tension partage une seule
// source (recette « Blackwell / RTX 50 lockout »). Pur et sans effet de bord → testable sans aucun appel NVAPI.
//
// La détection se fait par le nom commercial (par exemple « RTX 5080 » → Blackwell, « RTX 4070 » → Ada) :
// le signal sûr sans élévation et indépendant du pilote, déjà porté par GpuInfo.Model et par la
// lecture du nom du GPU par NVAPI. (NvAPI_GPU_GetArchInfo donnerait directement l'id
// d'architecture du silicium, mais son id de fonction n'est pas vérifié dans la recette : le
// classement par nom est la voie vérifiée, qui échoue proprement, utilisée ici.)
// -----------------------------------------------------------------------
public static class GpuArchitectureGate
{
    // « RTX 50xx » (toute série 50, modèle à 4 chiffres commençant par 50) = Blackwell.
    private static readonly Regex BlackwellModel =
        new(@"\bRTX\s*50\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // « RTX 40xx/30xx/20xx », « GTX 16xx/10xx » = Ada ou plus ancien (ancienne écriture de tension permise).
    private static readonly Regex AdaOrOlderModel =
        new(@"\b(RTX\s*(40|30|20)\d{2}|GTX\s*(16|10)\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // « RX 7xxx » = RDNA 3, « RX 9xxx » = RDNA 4. Meme discipline que cote NVIDIA : le nom
    // commercial est le signal lisible sans elevation et sans driver, donc utilisable avant
    // l'achat, ce que la matrice de couverture exige.
    private static readonly Regex Rdna3Model =
        new(@"\bRX\s*7\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Rdna2Model =
        new(@"\bRX\s*6\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Rdna4Model =
        new(@"\bRX\s*9\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Classe le nom commercial d'un GPU NVIDIA en architecture grossière.
    /// Nom inconnu ou non NVIDIA → Unknown (les gardes le traitent avec prudence).</summary>
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
        return GpuArchitecture.Unknown;
    }

    /// <summary>Vrai quand un GPU de cette architecture accepte l'écriture directe de
    /// sous-tension. Blackwell est verrouillé ; Ada ou plus ancien est permis ; Unknown est
    /// traité avec prudence comme non pris en charge (jamais d'écriture supposée sûre).</summary>
    public static bool SupportsVoltageUndervolt(GpuArchitecture architecture) => architecture switch
    {
        // Validees en ecriture sur une vraie carte.
        GpuArchitecture.AdaOrOlder => true,
        GpuArchitecture.Rdna4 => true,
        // Blackwell : NVIDIA a verrouille le chemin voltage.
        GpuArchitecture.Blackwell => false,
        // RDNA3 : la seule architecture AMD qui garde le voltage manuel, et la seule que
        // personne n'a jamais testee en ecriture. Ecrire un voltage sur du materiel jamais valide
        // est exactement ce que « fail-closed materiel » interdit.
        GpuArchitecture.Rdna3 => false,
        // Une architecture qu'on ne sait pas nommer n'est pas une architecture validee.
        _ => false,
    };

    /// <summary>Vrai quand ce GPU accepte l'écriture directe de sous-tension. Les GPU NVIDIA
    /// Blackwell (RTX 50) sont exclus (passer par limite de puissance + décalage cœur) ;
    /// les GPU non NVIDIA rendent vrai (la sous-tension ADLX d'AMD est une voie à part, non touchée).</summary>
    public static bool SupportsVoltageUndervolt(GpuInfo gpu)
    {
        var architecture = ClassifyByName(gpu.Model);

        // Rendre `true` pour tout GPU non-NVIDIA, au motif que « le verrou Blackwell ne concerne
        // pas AMD », serait vrai et insuffisant : ca laisserait passer RDNA3, la seule
        // architecture AMD qui garde le voltage manuel et la seule que personne n'a jamais
        // testee en ecriture.
        // RDNA2 pareil, jamais testée en écriture.
        if (architecture is GpuArchitecture.Rdna3 or GpuArchitecture.Rdna2)
            return false;

        var isNvidia = gpu.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        if (!isNvidia)
            // Les autres cartes AMD gardent leur chemin ADLX : la regle nomme RDNA3, et elargir le
            // refus a toutes les cartes non classees couperait des machines sur une supposition
            // plutot que sur un constat. La matrice de couverture, elle, dit ce qu'on ignore.
            return true;

        return architecture != GpuArchitecture.Blackwell;
    }
}
