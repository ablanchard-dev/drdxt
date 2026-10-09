using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// 06/10/2026 (plan 3.4, le mainteneur : « si de base le PC a un souci et qu'on fait le point de restauration sur le souci, il faut
/// en proposer un autre, sain »). Un point pris pendant un souci le contient. Quand un souci est en cours, le docteur
/// nomme le point le plus récent d'avant son début. Lecture seule : DrDXT ne restaure jamais.
/// </summary>
public static class PointDeRestaurationSain
{
    // Les mêmes fenêtres que le rapprochement des incidents (AvantLIncident) : une série = la même panne à moins de deux
    // semaines d'écart ; « en cours » = un incident dans les trente derniers jours. Choix non chiffrés, repris par cohérence.
    private static readonly TimeSpan Serie = TimeSpan.FromDays(14);
    private static readonly TimeSpan EnCours = TimeSpan.FromDays(30);

    /// <param name="lusDepuis">Revue T3+T4 (F-28) : le début de la fenêtre de lecture des incidents. Une série qui
    /// commence à moins de deux semaines de cette borne a peut-être commencé avant : elle est dite « au moins depuis ».</param>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<(DateTimeOffset Quand, string Nom)>? points,
        IReadOnlyList<FaitDate>? incidents, DateTimeOffset maintenant, DateTimeOffset? lusDepuis = null)
    {
        // Revue adverse du 09/10/2026 (P1) : journaux d'incidents illisibles, « aucun souci en cours » s'affichait.
        if (incidents is null)
        {
            yield return new RepairFinding("STABILITE", "point de restauration sain", null, null, RepairStatus.Unknown,
                "Les journaux d'incidents n'ont pas pu être lus : DrDXT ne sait pas si un souci est en cours, donc pas quel point préférer.");
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
        var tronque = lusDepuis is { } borne && debut.Quand - borne <= Serie;
        // 06/10/2026 (capture d'écran du scan) : l'avertissement disait « un souci est en cours » sans dire lequel. Le souci,
        // daté, est la provenance du constat ; l'écran le nomme.
        var souci = $"{debut.Libelle}, le {debut.Quand.ToLocalTime():dd/MM/yyyy à HH:mm}";
        var ouverture = tronque
            ? $"Un souci dure au moins depuis le {debut.Quand.ToLocalTime():dd/MM/yyyy} ({debut.Libelle}) ; il a peut-être "
              + "commencé avant, DrDXT ne lit que les 90 derniers jours"
            : $"Un souci a commencé le {debut.Quand.ToLocalTime():dd/MM/yyyy} ({debut.Libelle})";

        if (points is null)
        {
            yield return new RepairFinding("STABILITE", "point de restauration sain", null, null, RepairStatus.Unknown,
                $"{ouverture}, et la liste des points de restauration n'a pas pu être lue : DrDXT ne peut "
                + "pas dire lequel est d'avant.", Provenance: souci);
            yield break;
        }

        var avant = points.Where(p => p.Quand < debut.Quand).OrderByDescending(p => p.Quand).FirstOrDefault();
        var depuis = points.Count(p => p.Quand >= debut.Quand);
        if (avant.Nom is null)
        {
            yield return new RepairFinding("STABILITE", "point de restauration sain", "aucun point d'avant le souci", null,
                RepairStatus.Observed,
                $"{ouverture}, et aucun point de restauration n'est plus ancien. Un point créé maintenant "
                + "garderait le souci : il ne sert qu'à revenir à l'état d'aujourd'hui.", Provenance: souci);
            yield break;
        }

        yield return new RepairFinding("STABILITE", "point de restauration sain",
            $"« {avant.Nom} » du {avant.Quand.ToLocalTime():dd/MM/yyyy à HH:mm}", null, RepairStatus.Observed,
            $"{ouverture}. Ce point est d'avant{(tronque ? " ce que DrDXT a lu" : "")} : si tu restaures, c'est lui qu'il faut choisir (Restauration "
            + "du système, « Choisir un autre point de restauration »)."
            + (depuis > 0 ? $" {depuis} point{(depuis > 1 ? "s" : "")} pris depuis contien{(depuis > 1 ? "nent" : "t")} peut-être déjà le souci." : "")
            + " Restaurer retire les programmes et pilotes installés après ce point ; tes fichiers personnels ne bougent pas. "
            + "DrDXT ne restaure jamais tout seul.",
            RepairAction.Recommandation, souci);
    }
}
