using System.Text.RegularExpressions;

namespace DrDxt.Core;

/// <summary>
/// Sur un Ryzen de bureau 5000 et plus, le réglage du BIOS qui peut
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
            ? Docteur.T("CO.Msi")
            : Docteur.T("CO.Autre");
        return new RepairFinding("CPU", "Curve Optimizer (AMD)", null, null, RepairStatus.Observed,
            Docteur.T("CO.Explication", processeur.Trim(), chemin),
            RepairAction.Recommandation);
    }
}
