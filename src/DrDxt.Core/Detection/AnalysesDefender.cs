using System.Globalization;

namespace DrDxt.Core;

/// <summary>Une analyse de Microsoft Defender lue dans son journal : début, fin (null si elle n'est pas finie dans la
/// fenêtre lue), et si elle a été arrêtée avant la fin (événement 1002).</summary>
public sealed record AnalyseDefender(DateTimeOffset Debut, DateTimeOffset? Fin, bool Arretee);

/// <summary>
/// 07/10/2026 (chantier D3) : Microsoft Defender en lecture seule, du point de vue du joueur : ses analyses
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
                "le journal de Microsoft Defender n'a pas pu être lu : DrDXT ne sait pas si ses analyses tombent pendant tes "
                + "parties. « Non lu » n'est pas « aucune ».");

        var reglage = seulementInactif switch
        {
            true => " Ses analyses planifiées ne partent que quand le PC est inactif (réglage de Windows par défaut).",
            false => " Defender est réglé pour lancer ses analyses planifiées même quand le PC sert (le défaut de Windows "
                     + "est de ne les lancer qu'à l'inactivité).",
            null => "",
        };
        var arretees = analyses.Count(a => a.Arretee);
        var bilan = string.Format(CultureInfo.InvariantCulture, "{0} analyse{1} en {2} jours",
            analyses.Count, analyses.Count > 1 ? "s" : "", jours)
            + (arretees > 0 ? string.Format(CultureInfo.InvariantCulture, ", dont {0} arrêtée{1} avant la fin", arretees, arretees > 1 ? "s" : "") : "");

        if (parties.Count == 0)
            return new RepairFinding(Domaine, "analyses pendant tes parties", bilan, null,
                seulementInactif == false ? RepairStatus.Suspicious : RepairStatus.Observed,
                $"{bilan} ; aucune partie relevée : DrDXT ne peut pas encore les comparer à tes heures de jeu.{reglage}");

        var pendant = analyses.Where(a => parties.Any(p => a.Debut < p.Fin && (a.Fin ?? a.Debut) > p.Debut)).ToList();
        if (pendant.Count == 0)
            return new RepairFinding(Domaine, "analyses pendant tes parties", bilan, "aucune pendant une partie",
                seulementInactif == false ? RepairStatus.Suspicious : RepairStatus.ExpectedDefault,
                $"{bilan}, aucune pendant tes {parties.Count} partie{(parties.Count > 1 ? "s" : "")} relevée{(parties.Count > 1 ? "s" : "")}.{reglage}");

        var derniere = pendant.Max(a => a.Debut).ToLocalTime().ToString("dd/MM 'à' HH:mm", CultureInfo.GetCultureInfo("fr-FR"));
        return new RepairFinding(Domaine, "analyses pendant tes parties", bilan,
            string.Format(CultureInfo.InvariantCulture, "{0} pendant une partie", pendant.Count), RepairStatus.Suspicious,
            string.Format(CultureInfo.InvariantCulture, "{0}, dont {1} tombée{2} pendant une partie relevée (dernière le {3}) : "
                + "une analyse lit le disque et prend du processeur pendant que tu joues.{4} DrDXT ne touche pas à Defender.",
                bilan, pendant.Count, pendant.Count > 1 ? "s" : "", derniere, reglage));
    }
}
