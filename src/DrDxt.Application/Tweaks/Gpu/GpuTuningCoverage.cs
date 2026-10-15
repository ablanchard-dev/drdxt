using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

/// <summary>
/// Ce que cette machine recevra, dit avant le paiement.
///
/// Le problème que ça règle est commercial autant que technique : vendre du tuning GPU à
/// quelqu'un dont la carte est en lecture seule, c'est un remboursement immédiat et un avis
/// négatif. Le produit savait déjà refuser au moment d'appliquer : la sonde d'écriture-identité
/// le fait honnêtement. Mais elle le fait après l'achat, et derrière une élévation.
///
/// D'où cette classification : elle n'utilise que ce qui se lit sans élévation, sans driver et
/// sans rien écrire : le châssis, le fournisseur, la VRAM, le nom commercial. Exactement ce
/// qu'on peut montrer à quelqu'un qui n'a pas encore payé.
///
/// ══ ce qu'elle ne fait pas ══
///
/// Elle ne remplace pas la sonde. Un nom commercial ne prouve pas qu'un pilote honorera une
/// écriture : une carte OEM bridée, un portable déguisé, un pilote qui accepte et ne change
/// rien passeront ici et seront attrapés là-bas. La promesse tenue est donc bornée : « voilà ce
/// que ta machine devrait recevoir », jamais « voilà ce qui marchera à coup sûr ».
///
/// Et elle ne promet jamais par défaut. Une carte qu'on ne sait pas nommer ressort
/// <see cref="GpuTuningSupport.ReadOnly"/>, pas « supporté » : c'est la moitié de la décision
/// fail-closed, l'autre étant qu'on le dise.
/// </summary>
public enum GpuTuningSupport
{
    /// <summary>Architecture validée en vrai (écriture, relecture, revert). Le tuning est vendu.</summary>
    Validated,

    /// <summary>Lue et comprise, mais jamais validée en écriture, ou validée seulement en
    /// partie. « Supporté en lecture, tuning à venir » : on lit la carte, on ne la règle pas.</summary>
    ReadOnly,

    /// <summary>Rien à régler ici : GPU intégré, portable, châssis verrouillé. Ce n'est pas un
    /// manque du produit, c'est une propriété de la machine, et le dire évite un achat déçu.</summary>
    NotApplicable,
}

/// <summary>Une ligne de la matrice : la carte, ce qu'elle recevra, et pourquoi.</summary>
/// <param name="Model">Le nom commercial lu, tel quel.</param>
/// <param name="Support">Le verdict.</param>
/// <param name="Architecture">L'architecture reconnue, ou Unknown.</param>
/// <param name="ReasonKey">La clé resx du motif, jamais du texte codé en dur.</param>
/// <param name="VoltageTuning">Le voltage manuel est-il proposé sur cette carte ? Séparé du
/// verdict global : une RDNA3 est réglable (limite de puissance, fréquences) sans que le voltage
/// le soit, et confondre les deux ferait perdre des leviers réels ou en promettre un faux.</param>
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

    /// <summary>
    /// La matrice pour cette machine, une ligne par GPU. Vide seulement quand aucun GPU n'a été
    /// détecté, et ça se dit aussi, plutôt que d'afficher une matrice vide qui ressemble à une
    /// machine sans problème.
    /// </summary>
    /// <summary>
    /// Les architectures validées en vie (écriture, relecture, retour). Une architecture n'entre ici qu'avec
    /// son rapport dans tests/Shared/validations ; la garde <c>UnePreuveDebloqueSonMaterielTests</c> refuse le reste.
    /// Sans rapport archivé, une architecture reste « lecture seule » jusqu'à sa preuve.
    /// </summary>
    /// Ada validée par sa preuve, tests/Shared/validations/AdaOrOlder-rtx4060-2026-10-08.txt (limite de
    /// puissance d'une RTX 4060 posée 100 ⇒ 90 %, relue, rendue, relue à 100 %, par « Vérifier DrDXT » sur la machine).
    private static readonly System.Text.RegularExpressions.Regex Rtx40 =
        new(@"\bRTX\s*40\d{2}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static readonly IReadOnlySet<GpuArchitecture> ValideesEnVie = new HashSet<GpuArchitecture> { GpuArchitecture.AdaOrOlder };

    public static IReadOnlyList<GpuCoverageRow> For(HardwareInventory hw) => For(hw, ValideesEnVie);

    public static IReadOnlyList<GpuCoverageRow> For(HardwareInventory hw, IReadOnlySet<GpuArchitecture> validees)
    {
        if (hw?.Gpus is not { Count: > 0 }) return Array.Empty<GpuCoverageRow>();

        // Le châssis d'abord : sur un portable ou une console portable, aucune carte n'est
        // réglable, et l'annoncer carte par carte comme un défaut du produit serait faux.
        var lockedChassis = hw.Chassis != ChassisType.Desktop;

        return hw.Gpus.Select(g => Classify(g, lockedChassis, validees)).ToList();
    }

    private static GpuCoverageRow Classify(GpuInfo gpu, bool lockedChassis, IReadOnlySet<GpuArchitecture> validees)
    {
        var architecture = GpuArchitectureGate.ClassifyByName(gpu.Model);

        if (lockedChassis)
            return new(gpu.Model, GpuTuningSupport.NotApplicable, architecture,
                ReasonLaptopOrHandheld, VoltageTuning: false);

        // iGPU, carte mobile, fournisseur hors périmètre : il n'y a rien à régler, et ce n'est
        // pas un manque du produit.
        if (!GpuGating.IsTunableDiscreteGpu(gpu))
            return new(gpu.Model, GpuTuningSupport.NotApplicable, architecture,
                ReasonNotTunableHardware, VoltageTuning: false);

        var voltage = GpuArchitectureGate.SupportsVoltageUndervolt(gpu);

        return architecture switch
        {
            // Validée seulement avec son rapport archivé (voir ValideesEnVie). Le rapport
            // archivé est celui d'une RTX 4060 ; la classe AdaOrOlder ne se dit validée que pour les RTX 40 (Ada), les RTX
            // 20/30 et GTX 10/16 attendent le leur.
            GpuArchitecture.AdaOrOlder when validees.Contains(architecture) && Rtx40.IsMatch(gpu.Model) =>
                new(gpu.Model, GpuTuningSupport.Validated, architecture, ReasonValidated, voltage),
            GpuArchitecture.Rdna4 when validees.Contains(architecture) =>
                new(gpu.Model, GpuTuningSupport.Validated, architecture, ReasonValidated, voltage),

            // Blackwell : le reste du tuning marche, NVIDIA a verrouillé le voltage. Le dire
            // précisément vaut mieux que de classer toute la carte en lecture seule.
            GpuArchitecture.Blackwell when validees.Contains(architecture) =>
                new(gpu.Model, GpuTuningSupport.Validated, architecture, ReasonVoltageLockedByVendor, voltage),

            GpuArchitecture.AdaOrOlder or GpuArchitecture.Rdna4 or GpuArchitecture.Blackwell =>
                new(gpu.Model, GpuTuningSupport.ReadOnly, architecture, ReasonNeverWriteTested, voltage),

            // RDNA3 : jamais testée en écriture. C'est le cas que la règle nomme.
            GpuArchitecture.Rdna3 or GpuArchitecture.Rdna2 =>
                new(gpu.Model, GpuTuningSupport.ReadOnly, architecture, ReasonNeverWriteTested, voltage),

            // Une carte qu'on ne sait pas nommer n'est pas une carte validée. Lecture seule, et
            // on l'écrit : c'est le fail-closed dans sa forme la plus littérale.
            _ => new(gpu.Model, GpuTuningSupport.ReadOnly, architecture, ReasonUnknownArchitecture, voltage),
        };
    }
}
