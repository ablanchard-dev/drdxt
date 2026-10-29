using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu.Intel;

// -----------------------------------------------------------------------
// Ne pas se taire sur Intel : une Arc A770 est une carte dédiée (`CartesGraphiques.EstDediee`), mais le réglage de carte
// ne vise que NVIDIA et AMD (`GpuGating.IsTunableDiscreteGpu`). Sans carte Intel ni écriture :
//   * classer un GPU Intel (Arc discrète ou intégrée) ;
//   * dire qu'il n'y a pas de chemin de tuning Intel ;
//   * remonter les deux leviers sourcés chez Intel : le pilote game-ready (levier n°1) et ReBAR (requis pour une
//     performance optimale, BIOS donc guidance).
// L'écriture IGCL reste un lot à part entière.
// -----------------------------------------------------------------------

public enum IntelGpuClass
{
    /// <summary>Pas un GPU Intel.</summary>
    NotIntel = 0,
    /// <summary>iGPU (UHD, Iris, ou un « Arc Graphics » / « Arc 140V » soudé au CPU).</summary>
    Integrated = 1,
    /// <summary>Carte Arc discrète (A-series, B-series…).</summary>
    DiscreteArc = 2,
}

/// <summary>Un conseil Intel, guidance seule (aucun chemin d'application ici). <paramref name="MessageKey"/> est la clé
/// finale (`Finding.<Domaine><Sujet>`), jamais assemblée à la volée : elle se retrouve par recherche.</summary>
public sealed record IntelGpuGuidance(string Id, string Gpu, string MessageKey, string SourceUrl)
{
    public bool IsGuidanceOnly => true;
}

public static class IntelGpuAdvisor
{
    // Arc discrète = une lettre de série + 3 chiffres (A310/A380/A580/A750/A770, B570/B580, et la série suivante). Le seul
    // mot « Arc » classerait comme discrets les iGPU « Intel Arc Graphics » (Meteor Lake) et « Arc 140V » (Lunar Lake,
    // sans lettre de série).
    private static readonly Regex DiscreteArcModel =
        new(@"\bArc\b.*?\b[A-Z]\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Deuxième garde-fou : une carte discrète a de la VRAM dédiée, un iGPU en rapporte ~0.
    private const long DiscreteVramFloor = 1L << 30;

    private const string SrcArcDriver =
        "https://www.intel.com/content/www/us/en/products/docs/discrete-gpus/arc/software/gaming-technologies.html";
    private const string SrcArcRebar =
        "https://www.intel.com/content/www/us/en/support/articles/000092416/graphics.html";
    private const string SrcIgcl =
        "https://intel.github.io/drivers.gpu.control-library/Control/api.html";

    public static bool IsIntel(GpuInfo g) =>
        g.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    public static IntelGpuClass Classify(GpuInfo g)
    {
        if (!IsIntel(g)) return IntelGpuClass.NotIntel;
        return DiscreteArcModel.IsMatch(g.Model) && g.VramBytes >= DiscreteVramFloor
            ? IntelGpuClass.DiscreteArc
            : IntelGpuClass.Integrated;
    }

    /// <summary>Conseils pour les GPU Intel de la machine. Vide s'il n'y en a pas.</summary>
    public static IReadOnlyList<IntelGpuGuidance> Advise(IReadOnlyList<GpuInfo> gpus)
    {
        var advice = new List<IntelGpuGuidance>();
        foreach (var g in gpus)
        {
            if (Classify(g) != IntelGpuClass.DiscreteArc)
                continue; // heuristique : rien à dire d'utile sur un iGPU que les leviers OS ne couvrent déjà.

            // (1) Le tuning GPU est refusé sur cette carte : on dit lequel et pourquoi.
            advice.Add(new IntelGpuGuidance(
                "gpu.intel.no_tuning_path", g.Model,
                "Finding.Gpu.IntelNoTuningPath", SrcIgcl));

            // (2) ReBAR, requis pour une performance optimale sur Arc selon Intel ; BIOS ⇒ guidance. Unknown compte : la
            // lecture ReBAR Intel n'est pas câblée, et « je ne sais pas » sur un levier à deux chiffres se dit.
            if (g.ReBar != TriState.On)
                advice.Add(new IntelGpuGuidance(
                    "gpu.intel.rebar_required", g.Model,
                    g.ReBar == TriState.Off
                        ? "Finding.Gpu.IntelRebarOff"
                        : "Finding.Gpu.IntelRebarUnknown",
                    SrcArcRebar));

            // (3) Le pilote, levier n°1 sur Arc (optimisations par titre, surtout DX9/DX11) : le plancher de fraîcheur est trop
            // grossier pour la cadence Arc, donc on le dit, plutôt qu'un silence lu « ton pilote va bien ».
            advice.Add(new IntelGpuGuidance(
                "gpu.intel.driver_is_the_lever", g.Model,
                "Finding.Gpu.IntelDriverIsTheLever", SrcArcDriver));
        }
        return advice;
    }
}
