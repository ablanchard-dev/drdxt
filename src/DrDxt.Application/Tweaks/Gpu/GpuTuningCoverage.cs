using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

/// <summary>
/// Ce que cette machine recevra, dit d'avance. La sonde d'écriture-identité refuse au moment d'appliquer, derrière une
/// élévation ; cette classification ne lit que ce qui se lit sans élévation, sans pilote et sans écrire (châssis,
/// fournisseur, VRAM, nom commercial). Elle ne remplace pas la sonde : une carte OEM bridée, un portable déguisé, un
/// pilote qui accepte sans rien changer passent ici et sont attrapés là-bas. D'où « voilà ce que ta machine devrait
/// recevoir », jamais « ce qui marchera à coup sûr ». Une carte qu'on ne sait pas nommer ressort
/// <see cref="GpuTuningSupport.ReadOnly"/>, et c'est dit.
/// </summary>
public enum GpuTuningSupport
{
    /// <summary>Architecture validée en vrai (écriture, relecture, revert) : le tuning est proposé.</summary>
    Validated,

    /// <summary>Lue et comprise, jamais (ou en partie seulement) validée en écriture : on lit la carte, on ne la règle
    /// pas.</summary>
    ReadOnly,

    /// <summary>Rien à régler ici (GPU intégré, portable, châssis verrouillé) : une propriété de la machine, pas un
    /// manque du produit.</summary>
    NotApplicable,
}

/// <summary>Une ligne de la matrice : la carte, ce qu'elle recevra, et pourquoi.</summary>
/// <param name="Model">Le nom commercial lu, tel quel.</param>
/// <param name="Support">Le verdict.</param>
/// <param name="Architecture">L'architecture reconnue, ou Unknown.</param>
/// <param name="ReasonKey">La clé resx du motif, jamais du texte codé en dur.</param>
/// <param name="VoltageTuning">Le voltage manuel est-il proposé ? Séparé du verdict : une RDNA3 est réglable (limite de
/// puissance, fréquences) sans que le voltage le soit.</param>
public readonly record struct GpuCoverageRow(
    string Model,
    GpuTuningSupport Support,
    GpuArchitecture Architecture,
    string ReasonKey,
    bool VoltageTuning);

public static class GpuTuningCoverage
{
    // Clés resx des motifs. Fermées : un motif inventé serait un motif qu'on ne peut pas traduire.
    public const string ReasonValidated = "Coverage.Reason.Validated";
    public const string ReasonNeverWriteTested = "Coverage.Reason.NeverWriteTested";
    public const string ReasonUnknownArchitecture = "Coverage.Reason.UnknownArchitecture";
    public const string ReasonVoltageLockedByVendor = "Coverage.Reason.VoltageLockedByVendor";
    public const string ReasonNotTunableHardware = "Coverage.Reason.NotTunableHardware";
    public const string ReasonLaptopOrHandheld = "Coverage.Reason.LaptopOrHandheld";

    // La preuve d'Ada est celle d'une RTX 4060 (tests/Shared/validations/AdaOrOlder-rtx4060-2026-10-08.txt) : la classe
    // ne se dit validée que pour les RTX 40.
    private static readonly System.Text.RegularExpressions.Regex Rtx40 =
        new(@"\bRTX\s*40\d{2}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Les architectures validées en vie (écriture, relecture, retour), chacune avec son rapport dans
    /// tests/Shared/validations (garde <c>UnePreuveDebloqueSonMaterielTests</c>) ; sinon « lecture seule ». Ada :
    /// AdaOrOlder-rtx4060-2026-10-08.txt (mesuré : limite de puissance d'une RTX 4060 posée 100 ⇒ 90 %, relue, rendue, relue à
    /// 100 %, par « Vérifier DrDXT »).
    /// </summary>
    public static readonly IReadOnlySet<GpuArchitecture> ValideesEnVie = new HashSet<GpuArchitecture> { GpuArchitecture.AdaOrOlder };

    /// <summary>
    /// La matrice pour cette machine, une ligne par GPU ; vide seulement sans GPU détecté, et ça se dit aussi.
    /// </summary>
    public static IReadOnlyList<GpuCoverageRow> For(HardwareInventory hw) => For(hw, ValideesEnVie);

    public static IReadOnlyList<GpuCoverageRow> For(HardwareInventory hw, IReadOnlySet<GpuArchitecture> validees)
    {
        if (hw?.Gpus is not { Count: > 0 }) return Array.Empty<GpuCoverageRow>();

        // Le châssis d'abord : sur un portable ou une console portable, aucune carte n'est réglable.
        var lockedChassis = hw.Chassis != ChassisType.Desktop;

        return hw.Gpus.Select(g => Classify(g, lockedChassis, validees)).ToList();
    }

    private static GpuCoverageRow Classify(GpuInfo gpu, bool lockedChassis, IReadOnlySet<GpuArchitecture> validees)
    {
        var architecture = GpuArchitectureGate.ClassifyByName(gpu.Model);

        if (lockedChassis)
            return new(gpu.Model, GpuTuningSupport.NotApplicable, architecture,
                ReasonLaptopOrHandheld, VoltageTuning: false);

        // iGPU, carte mobile, fournisseur hors périmètre : rien à régler.
        if (!GpuGating.IsTunableDiscreteGpu(gpu))
            return new(gpu.Model, GpuTuningSupport.NotApplicable, architecture,
                ReasonNotTunableHardware, VoltageTuning: false);

        var voltage = GpuArchitectureGate.SupportsVoltageUndervolt(gpu);

        return architecture switch
        {
            // Le rapport archivé est celui d'une RTX 4060 : AdaOrOlder ne se dit validée que pour les RTX 40, les RTX 20 et 30
            // et GTX 10 et 16 attendent le leur.
            GpuArchitecture.AdaOrOlder when validees.Contains(architecture) && Rtx40.IsMatch(gpu.Model) =>
                new(gpu.Model, GpuTuningSupport.Validated, architecture, ReasonValidated, voltage),
            // Radeon : une seule règle pour toutes les architectures, chacune ouverte par son propre rapport.
            GpuArchitecture.Gcn or GpuArchitecture.Rdna1 or GpuArchitecture.Rdna2 or GpuArchitecture.Rdna3
                or GpuArchitecture.Rdna4 when validees.Contains(architecture) =>
                new(gpu.Model, GpuTuningSupport.Validated, architecture, ReasonValidated, voltage),

            // Blackwell : NVIDIA a verrouillé le voltage, le reste du tuning marche.
            GpuArchitecture.Blackwell when validees.Contains(architecture) =>
                new(gpu.Model, GpuTuningSupport.Validated, architecture, ReasonVoltageLockedByVendor, voltage),

            // Reconnue mais sans rapport : jamais testée en écriture, lecture seule.
            GpuArchitecture.AdaOrOlder or GpuArchitecture.Blackwell or GpuArchitecture.Gcn or GpuArchitecture.Rdna1
                or GpuArchitecture.Rdna2 or GpuArchitecture.Rdna3 or GpuArchitecture.Rdna4 =>
                new(gpu.Model, GpuTuningSupport.ReadOnly, architecture, ReasonNeverWriteTested, voltage),

            // Une carte qu'on ne sait pas nommer : lecture seule, et on l'écrit (fail-closed).
            _ => new(gpu.Model, GpuTuningSupport.ReadOnly, architecture, ReasonUnknownArchitecture, voltage),
        };
    }
}
