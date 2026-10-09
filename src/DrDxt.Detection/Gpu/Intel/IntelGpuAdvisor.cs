using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu.Intel;

// -----------------------------------------------------------------------
// NS-5, première tranche : Arrêter de se taire sur intel.
//
// Le produit prétend couvrir 2015-2026 et les trois fabricants. Sur une machine Arc,
// il ne dit rien : `GpuGating.IsDiscrete` exclut Intel par construction, donc une Arc
// A770 est classée comme un iGPU, aucun levier GPU ne s'applique, et l'utilisateur
// n'est pas prévenu. C'est un skip silencieux — exactement ce que le cahier interdit
// (§22.3), et la recherche du projet (non publiée) le nomme déjà.
//
// Cette tranche ne demande aucune carte Intel et n'écrit rien :
//   * classer correctement un GPU Intel (Arc discrète vs intégrée),
//   * dire honnêtement qu'il n'y a pas de chemin de tuning Intel,
//   * remonter les deux leviers qui comptent vraiment sur Arc, tous deux sourcés
//     chez Intel : le pilote game-ready (levier n°1) et ReBAR (requis pour une
//     performance optimale, BIOS donc guidance).
//
// L'écriture IGCL reste un lot à part entière. Ici on répare l'honnêteté, pas la
// couverture de tuning.
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

/// <summary>Un conseil Intel. Guidance par CONSTRUCTION : aucun chemin d'application
/// n'existe dans ce namespace. <paramref name="MessageKey"/> est la clé de ressource FINALE
/// (convention `Finding.<Domaine><Sujet>`, comme `Finding.Memory.PagefileDisabled`) — pas un
/// fragment à recomposer : une clé assemblée à la volée ne se retrouve pas par recherche.</summary>
public sealed record IntelGpuGuidance(string Id, string Gpu, string MessageKey, string SourceUrl)
{
    public bool IsGuidanceOnly => true;
}

public static class IntelGpuAdvisor
{
    // Arc discrète = une lettre de série + 3 chiffres : A310/A380/A580/A750/A770,
    // B570/B580, et la série suivante quelle qu'elle soit.
    //
    // Le piège que ce motif évite : Intel appelle aussi « Arc » des iGPU — « Intel Arc
    // Graphics » (Meteor Lake) et « Arc 140V » (Lunar Lake). Chercher le simple mot « Arc »
    // classerait un iGPU soudé comme une carte discrète et proposerait de l'overclocker.
    // « Arc 140V » n'a pas de lettre de série, il ne matche pas.
    private static readonly Regex DiscreteArcModel =
        new(@"\bArc\b.*?\b[A-Z]\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Une carte discrète a de la VRAM dédiée ; un iGPU partage la mémoire système et en
    // rapporte ~0. Deuxième garde-fou, au cas où le nom mentirait.
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

            // (1) Ne pas se taire. Le tuning GPU est refusé sur cette carte — on dit lequel
            // et pourquoi, au lieu de la faire disparaître du rapport.
            advice.Add(new IntelGpuGuidance(
                "gpu.intel.no_tuning_path", g.Model,
                "Finding.Gpu.IntelNoTuningPath", SrcIgcl));

            // (2) ReBAR : Intel dit qu'il est REQUIS pour une performance optimale sur Arc.
            // La carte tourne sans, mais sous-performe. BIOS ⇒ guidance, jamais d'écriture.
            // Unknown compte ici : sur Intel la lecture ReBAR n'est pas câblée, et « je ne sais
            // pas » sur un levier à deux chiffres de pourcentage mérite d'être dit.
            if (g.ReBar != TriState.On)
                advice.Add(new IntelGpuGuidance(
                    "gpu.intel.rebar_required", g.Model,
                    g.ReBar == TriState.Off
                        ? "Finding.Gpu.IntelRebarOff"
                        : "Finding.Gpu.IntelRebarUnknown",
                    SrcArcRebar));

            // (3) Le pilote est le levier n°1 sur Arc (optimisations par titre, surtout
            // DX9/DX11). Notre détecteur de fraîcheur est vendor-agnostique et son plancher
            // est trop grossier pour la cadence Arc : il ne dira jamais qu'un pilote Arc de
            // six mois est en retard. Donc on le DIT, au lieu de laisser croire que le
            // silence vaut « ton pilote va bien ».
            advice.Add(new IntelGpuGuidance(
                "gpu.intel.driver_is_the_lever", g.Model,
                "Finding.Gpu.IntelDriverIsTheLever", SrcArcDriver));
        }
        return advice;
    }
}
