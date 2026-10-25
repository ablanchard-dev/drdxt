using System.Globalization;

namespace DrDxt.Core;

/// <summary>Une analyse de Microsoft Defender lue dans son journal : début, fin (null si elle n'est pas finie dans la
/// fenêtre lue : encore en cours, ou coupée par un redémarrage), et si elle a été arrêtée avant la fin (événement 1002).</summary>
public sealed record AnalyseDefender(DateTimeOffset Debut, DateTimeOffset? Fin, bool Arretee);

/// <summary>
/// Microsoft Defender en lecture seule, du point de vue du joueur : ses analyses
/// tombent-elles pendant les parties ? Sources (Microsoft Learn) : « Microsoft Defender Antivirus event IDs » (1000 début
/// d'analyse, 1001 fin, 1002 analyse arrêtée avant la fin) et « Configure scheduled quick or full Microsoft Defender
/// Antivirus scans » (ScanOnlyIfIdleEnabled : l'analyse planifiée n'a lieu que si le PC est inactif). DrDXT ne règle rien
/// dans Defender et ne propose aucune exclusion : c'est la sécurité du PC.
/// </summary>
public static class AnalysesDefender
{
    public const string Domaine = "DEFENDER";

    /// <summary>Le constat. <paramref name="analyses"/> null = journal non lu, jamais « aucune ».</summary>
    public static RepairFinding Juger(IReadOnlyList<AnalyseDefender>? analyses, IReadOnlyList<PlageDeJeu> parties,
        bool? seulementInactif, int jours)
    {
        if (analyses is null)
            return new RepairFinding(Domaine, "analyses pendant tes parties", null, null, RepairStatus.Unknown,
                Docteur.T("AD.NonLu"));

        var reglage = seulementInactif switch
        {
            true => Docteur.T("AD.Inactif"),
            false => Docteur.T("AD.MemeActif"),
            null => "",
        };
        var arretees = analyses.Count(a => a.Arretee);
        var bilan = (analyses.Count == 1 ? Docteur.T("AD.Bilan1", jours) : Docteur.T("AD.Bilan", analyses.Count, jours))
            + (arretees == 1 ? Docteur.T("AD.Arretee1") : arretees > 1 ? Docteur.T("AD.Arretees", arretees) : "");

        if (parties.Count == 0)
            return new RepairFinding(Domaine, "analyses pendant tes parties", bilan, null,
                seulementInactif == false ? RepairStatus.Suspicious : RepairStatus.Observed,
                Docteur.T("AD.SansPartie", bilan, reglage));

        // Sans fin lue, l'analyse est encore en cours ou a été coupée (redémarrage) : elle couvre au plus jusqu'au début de
        // l'analyse suivante, jamais toutes les parties d'après.
        var debuts = analyses.Select(a => a.Debut).Order().ToList();
        DateTimeOffset Fin(AnalyseDefender a) => a.Fin ?? debuts.FirstOrDefault(d => d > a.Debut, DateTimeOffset.MaxValue);
        var pendant = analyses.Where(a => parties.Any(p => a.Debut < p.Fin && Fin(a) > p.Debut)).ToList();
        if (pendant.Count == 0)
            return new RepairFinding(Domaine, "analyses pendant tes parties", bilan, Docteur.T("AD.AucunePendant"),
                seulementInactif == false ? RepairStatus.Suspicious : RepairStatus.ExpectedDefault,
                parties.Count == 1 ? Docteur.T("AD.AucuneSur1", bilan, reglage) : Docteur.T("AD.AucuneSur", bilan, parties.Count, reglage));

        var derniere = pendant.Max(a => a.Debut).ToLocalTime().ToString(Docteur.T("AD.Date"), CultureInfo.InvariantCulture);
        return new RepairFinding(Domaine, "analyses pendant tes parties", bilan,
            Docteur.T("AD.Pendant", pendant.Count), RepairStatus.Suspicious,
            Docteur.T(pendant.Count == 1 ? "AD.Tombee1" : "AD.Tombee", bilan, pendant.Count, derniere, reglage));
    }
}
