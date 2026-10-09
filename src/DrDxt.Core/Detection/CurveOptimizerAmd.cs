using System.Text.RegularExpressions;

namespace DrDxt.Core;

/// <summary>
/// 07/10/2026 (chantier A7, demande du 27/06) : sur un Ryzen de bureau 5000 et plus, le réglage du BIOS qui peut
/// encore rendre des images : Curve Optimizer (Precision Boost Overdrive), une baisse de tension cœur par cœur qui laisse
/// la puce monter plus haut. Source : MSI, « How to Use Curve Optimizer to Lower Ryzen 9 9950X3D Temperatures and Boost
/// Performance » (msi.com/blog, relevé le 07/10/2026) : chemin du menu dans Click BIOS X, une valeur négative baisse la
/// tension, environ +6 % sur Cinebench R23 à -31/-25 sur leur 9950X3D, instabilité possible, avancer par petits pas et
/// tester. Un guide, jamais une écriture : DrDXT n'écrit pas dans le BIOS. Les portables (suffixes U, H, HS, HX) n'ont
/// en général pas ce menu : rien n'est dit (la fin de mot après le numéro les écarte déjà, « 7840HS » n'en a pas).
/// </summary>
public static class CurveOptimizerAmd
{
    private static readonly Regex RyzenDeBureau = new(@"\bRyzen\s+(?:[3579]|Threadripper)\s+(?<serie>[5789])\d{3}(?<suffixe>X3D|XT|X|G|GT|F)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Le constat, ou <c>null</c> : pas un Ryzen de bureau de série 5000 et plus, ou processeur non lu.</summary>
    public static RepairFinding? Juger(string? processeur, string? fabricantCarte)
    {
        if (processeur is null || !RyzenDeBureau.IsMatch(processeur)) return null;
        var msi = fabricantCarte?.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) == true
                  || fabricantCarte?.Contains("MSI", StringComparison.Ordinal) == true;
        var chemin = msi
            ? "Sur ta carte MSI (Click BIOS X) : Overclocking\\Advanced CPU Configuration\\AMD Overclocking\\Precision Boost Overdrive, "
              + "choisir Advanced, puis Curve Optimizer, signe Negative et une petite valeur."
            : "Sur ta carte, le réglage porte le même nom (Curve Optimizer, dans Precision Boost Overdrive) ; son chemin est dans "
              + "le manuel de la carte. DrDXT n'en connaît le chemin que sur les cartes MSI.";
        return new RepairFinding("CPU", "Curve Optimizer (AMD)", null, null, RepairStatus.Observed,
            $"Ton {processeur.Trim()} accepte Curve Optimizer dans le BIOS : une baisse de tension cœur par cœur qui peut le laisser "
            + "monter plus haut. MSI a mesuré environ +6 % sur Cinebench R23 sur un Ryzen 9 9950X3D à -31/-25 (https://www.msi.com/blog/how-to-use-curve-optimizer-to-lower-ryzen-9-9950x3d-temperatures-and-boost-performance) ; chez toi le gain "
            + "n'est pas mesuré, chaque puce a sa marge. " + chemin + " MSI prévient : instabilité possible, avancer par petits pas "
            + "et tester la stabilité à chaque pas. Si des arrêts brutaux ou des erreurs matérielles (WHEA) apparaissent ensuite, "
            + "DrDXT les signale : reviens à la valeur d'avant. DrDXT n'écrit jamais dans le BIOS.",
            RepairAction.Recommandation);
    }
}
