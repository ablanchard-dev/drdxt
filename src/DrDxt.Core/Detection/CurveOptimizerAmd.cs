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
/// Les X3D de la série 5000 (5600X3D, 5700X3D, 5800X3D) sont verrouillés par AMD, PBO et Curve Optimizer compris ;
/// certains fabricants de cartes ont rouvert un Curve Optimizer négatif par une mise à jour du BIOS (AGESA 1.2.0.8 et
/// plus), sans que DrDXT sache lire laquelle : le conseil le dit au conditionnel au lieu d'affirmer que la puce l'accepte.
/// </summary>
public static class CurveOptimizerAmd
{
    private static readonly Regex RyzenDeBureau = new(@"\bRyzen\s+(?:[3579]|Threadripper)\s+(?<serie>[5789])\d{3}(?<suffixe>X3D|XT|X|G|GT|F)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Le constat, ou <c>null</c> : pas un Ryzen de bureau de série 5000 et plus, ou processeur non lu.
    /// <paramref name="arrets"/> : les arrêts brutaux hors bouton (null = journal non lu). Une baisse de tension pousse la
    /// puce : sur une machine qui s'arrête déjà, ou dont on n'a pas pu lire les arrêts, le conseil devient « d'abord
    /// stabiliser ». Un seul arrêt suffit, en veille compris (une baisse de tension instable lâche souvent au repos), et
    /// le conseil revient seul après <see cref="CandidateSelector.FenetreInstabilite"/> sans arrêt.
    /// <paramref name="autresIncidents"/> : les autres incidents datés et lus (écran bleu vu par son rapport ou son vidage,
    /// erreur matérielle grave, hausse des arrêts dangereux du SSD) ; un seul suffit aussi. Une source non lue se passe
    /// en <paramref name="arrets"/> = null : non lu vaut instable, jamais stable.
    /// <paramref name="lu"/> : le Curve Optimizer lu dans le BIOS (<see cref="LectureCurveOptimizer"/>) ; déjà actif, on
    /// dit sa valeur et on ne conseille pas d'aller plus loin. Null = non lu : le conseil reste celui d'avant.</summary>
    public static RepairFinding? Juger(string? processeur, string? fabricantCarte, IReadOnlyList<ArretClasse>? arrets, DateTimeOffset maintenant,
        IReadOnlyList<DateTimeOffset>? autresIncidents = null, CurveOptimizerLu? lu = null)
    {
        if (processeur is null || !RyzenDeBureau.IsMatch(processeur)) return null;
        var jours = CandidateSelector.FenetreInstabilite.Days;
        if (arrets is null)
            return new RepairFinding("CPU", "Curve Optimizer (AMD)", null, null, RepairStatus.Unknown,
                Docteur.T("CO.StabiliteNonLue"));
        var recents = arrets.Select(a => a.Quand).Concat(autresIncidents ?? [])
            .Where(d => maintenant - d <= CandidateSelector.FenetreInstabilite).ToList();
        if (recents.Count > 0)
            return new RepairFinding("CPU", "Curve Optimizer (AMD)", null, null, RepairStatus.Observed,
                Docteur.T("CO.DabordStabiliser", recents.Count, jours, recents.Max().ToLocalTime()));
        if (lu is { Actif: true })
            return new RepairFinding("CPU", "Curve Optimizer (AMD)", LectureCurveOptimizer.Texte(lu), null, RepairStatus.Observed,
                Docteur.T("CO.DejaActif", processeur.Trim(), LectureCurveOptimizer.Texte(lu)));
        var msi = fabricantCarte?.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) == true
                  || fabricantCarte?.Contains("MSI", StringComparison.Ordinal) == true;
        var chemin = msi
            ? Docteur.T("CO.Msi")
            : Docteur.T("CO.Autre");
        var m = RyzenDeBureau.Match(processeur);
        var x3dVerrouille = m.Groups["serie"].Value == "5" && m.Groups["suffixe"].Value.Equals("X3D", StringComparison.OrdinalIgnoreCase);
        return new RepairFinding("CPU", "Curve Optimizer (AMD)", null, null, RepairStatus.Observed,
            Docteur.T(x3dVerrouille ? "CO.ExplicationX3D5000" : "CO.Explication", processeur.Trim(), chemin),
            RepairAction.Recommandation);
    }
}
