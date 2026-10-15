using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Un point pris pendant un souci le contient. Quand un souci est en cours, le docteur
/// nomme le point le plus récent d'avant son début. Lecture seule : DrDXT ne restaure jamais.
/// </summary>
public static class PointDeRestaurationSain
{
    // Les mêmes fenêtres que le rapprochement des incidents (AvantLIncident) : une série = la même panne à moins de deux
    // semaines d'écart ; « en cours » = un incident dans les trente derniers jours. Choix non chiffrés, repris par cohérence.
    private static readonly TimeSpan Serie = TimeSpan.FromDays(14);
    private static readonly TimeSpan EnCours = TimeSpan.FromDays(30);

    /// <summary>Un incident seul n'est un souci en cours que s'il a moins de trois jours ;
    /// au-delà, sans récidive, c'est un accident passé (le constat « Arrêts brutaux » le garde dans son bilan).</summary>
    public static readonly TimeSpan IsoleEnCours = TimeSpan.FromDays(3);

    /// <param name="lusDepuis">Le début de la fenêtre de lecture des incidents. Une série qui
    /// commence à moins de deux semaines de cette borne a peut-être commencé avant : elle est dite « au moins depuis ».</param>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<(DateTimeOffset Quand, string Nom)>? points,
        IReadOnlyList<FaitDate>? incidents, DateTimeOffset maintenant, DateTimeOffset? lusDepuis = null)
    {
        // Journaux d'incidents illisibles, « aucun souci en cours » s'affichait.
        if (incidents is null)
        {
            yield return new RepairFinding("STABILITE", "point de restauration sain", null, null, RepairStatus.Unknown,
                Docteur.T("PR.NonLu"));
            yield break;
        }
        var tries = incidents.OrderByDescending(i => i.Quand).ToList();
        if (tries.Count == 0 || maintenant - tries[0].Quand > EnCours) yield break;

        // Le début de la série : on remonte tant que l'incident précédent est à moins de deux semaines.
        var debut = tries[0];
        foreach (var i in tries.Skip(1))
        {
            if (debut.Quand - i.Quand > Serie) break;
            debut = i;
        }
        if (ReferenceEquals(debut, tries[0]) && maintenant - debut.Quand > IsoleEnCours) yield break;
        var tronque = lusDepuis is { } borne && debut.Quand - borne <= Serie;
        // « Un souci est en cours » ne dit pas lequel : le souci, daté, est la provenance du constat,
        // et l'écran le nomme.
        var souci = Docteur.T("PR.Souci", debut.Libelle, debut.Quand.ToLocalTime());
        var ouverture = tronque
            ? Docteur.T("PR.DureDepuis", debut.Quand.ToLocalTime(), debut.Libelle)
            : Docteur.T("PR.ACommence", debut.Quand.ToLocalTime(), debut.Libelle);

        if (points is null)
        {
            yield return new RepairFinding("STABILITE", "point de restauration sain", null, null, RepairStatus.Unknown,
                Docteur.T("PR.PointsNonLus", ouverture), Provenance: souci);
            yield break;
        }

        var avant = points.Where(p => p.Quand < debut.Quand).OrderByDescending(p => p.Quand).FirstOrDefault();
        var depuis = points.Count(p => p.Quand >= debut.Quand);
        if (avant.Nom is null)
        {
            yield return new RepairFinding("STABILITE", "point de restauration sain", Docteur.T("PR.AucunAvant"), null,
                RepairStatus.Observed, Docteur.T("PR.AucunAvantExplication", ouverture), Provenance: souci);
            yield break;
        }

        yield return new RepairFinding("STABILITE", "point de restauration sain",
            Docteur.T("PR.Point", avant.Nom, avant.Quand.ToLocalTime()), null, RepairStatus.Observed,
            Docteur.T("PR.Choisir", ouverture, tronque ? Docteur.T("PR.CeQueLu") : "")
            + (depuis > 0 ? Docteur.T(depuis > 1 ? "PR.DepuisPlusieurs" : "PR.DepuisUn", depuis) : "")
            + Docteur.T("PR.Restaurer"),
            RepairAction.Recommandation, souci);
    }
}
