using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>
/// 04/10/2026 (plan 3.2) : un changement (réglage du BIOS, levier, bascule) suivi d'un arrêt brutal ou d'une erreur
/// matérielle dans la semaine est la première piste ; le docteur rapprochait déjà les incidents des pilotes installés,
/// pas des réglages. Une semaine sans incident après un changement se dit aussi : c'est ce qui permet de le garder.
/// Pur : le journal des changements, les dates d'arrêts et d'erreurs, et l'heure, viennent de l'appelant.
/// </summary>
public static class StabiliteApresChangement
{
    public static readonly TimeSpan Surveillance = TimeSpan.FromDays(7);

    /// <summary>Revue adverse du 09/10/2026 (R7) : un journal d'arrêts ou d'erreurs illisible, et des changements récents à
    /// suivre : « non lu ». Sans changement récent, rien à rapprocher, rien à dire.</summary>
    public static IEnumerable<RepairFinding> NonLu(IReadOnlyList<Changement> changements, DateTimeOffset maintenant)
        => changements.Any(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true && maintenant - c.Quand <= TimeSpan.FromDays(30))
            ? [new RepairFinding("STABILITE", "après un changement", null, null, RepairStatus.Unknown,
                "le journal des arrêts ou celui des erreurs matérielles n'a pas pu être lu : DrDXT ne sait pas si tes changements "
                + "récents ont été suivis d'un incident.")]
            : [];

    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Changement> changements, IReadOnlyList<DateTimeOffset> arrets,
        IReadOnlyList<DateTimeOffset> erreursMaterielles, DateTimeOffset maintenant)
    {
        string D(DateTimeOffset d) => d.LocalDateTime.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
        // UI1 (08/10/2026) : « 1 arrêt(s) brutal(aux) » vu sur capture ; le nombre choisit sa forme (moins de 2 : singulier).
        static string N(int n, string un, string plusieurs) => $"{n} {(n < 2 ? un : plusieurs)}";
        static string Jours(double j)
        {
            var arrondi = Math.Round(j, 1);
            return $"{arrondi.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR"))} {(arrondi < 2 ? "jour" : "jours")}";
        }

        // Chaque constat garde le rang de son changement : la sortie suit l'ordre des changements (revue n° 15).
        var sortie = new List<(int Rang, RepairFinding F)>();
        var suspects = new List<(int Rang, Changement C, List<DateTimeOffset> A, List<DateTimeOffset> W)>();
        var rang = 0;
        foreach (var c in changements.Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true
                                                 && maintenant - c.Quand <= TimeSpan.FromDays(30)))
        {
            rang++;
            var fin = c.Quand + Surveillance;
            var a = arrets.Where(d => d > c.Quand && d <= fin).OrderBy(d => d).ToList();
            var w = erreursMaterielles.Where(d => d > c.Quand && d <= fin).OrderBy(d => d).ToList();
            if (a.Count + w.Count > 0)
                suspects.Add((rang, c, a, w));
            else if (maintenant < fin)
                sortie.Add((rang, new RepairFinding("STABILITE", "après un changement", "aucun incident pour l'instant", "aucun incident",
                    RepairStatus.Observed,
                    $"depuis {c.Libelle} ({D(c.Quand)}) : aucun arrêt brutal ni erreur matérielle, après {Jours((maintenant - c.Quand).TotalDays)} "
                    + $"sur les {Surveillance.TotalDays:0} de surveillance.")));
        }

        // UI1 (08/10/2026), revue n° 15 : les changements qui partagent AU MOINS un incident se disent ensemble, de proche en
        // proche (A voit l'arrêt du jour 5, B ceux des jours 5 et 9 : un seul constat, qui n'accuse aucun des deux).
        var groupe = Enumerable.Range(0, suspects.Count).ToArray();
        int Racine(int i) { while (groupe[i] != i) i = groupe[i] = groupe[groupe[i]]; return i; }
        for (var i = 0; i < suspects.Count; i++)
            for (var j = i + 1; j < suspects.Count; j++)
                if (suspects[i].A.Intersect(suspects[j].A).Any() || suspects[i].W.Intersect(suspects[j].W).Any())
                    groupe[Racine(j)] = Racine(i);

        foreach (var g in Enumerable.Range(0, suspects.Count).GroupBy(Racine).Select(g => g.Select(i => suspects[i]).ToList()))
        {
            if (g.Count > 1)
            {
                var a = g.SelectMany(x => x.A).Distinct().Order().ToList();
                var w = g.SelectMany(x => x.W).Distinct().Order().ToList();
                var faitsCommuns = new List<string>();
                if (a.Count > 0) faitsCommuns.Add($"{N(a.Count, "arrêt brutal", "arrêts brutaux")} (le {(a.Count == 1 ? "" : "premier le ")}{D(a[0])})");
                if (w.Count > 0) faitsCommuns.Add($"{N(w.Count, "erreur matérielle", "erreurs matérielles")} (la première le {D(w[0])})");
                var liste = string.Join(" ; ", g.Select(x => $"{x.C.Libelle} ({D(x.C.Quand)})"));
                sortie.Add((g.Min(x => x.Rang), new RepairFinding("STABILITE", "après un changement", string.Join(", ", faitsCommuns), "aucun incident",
                    RepairStatus.Suspicious,
                    $"dans la semaine qui a suivi {g.Count} changements ({liste}) : {string.Join(" et ", faitsCommuns)}. C'est un "
                    + "rapprochement de dates, pas une preuve, et il ne désigne aucun d'eux en particulier : reviens sur eux un à la "
                    + "fois, le plus récent d'abord, puis regarde si les incidents s'arrêtent.",
                    RepairAction.Recommandation)));
                continue;
            }
            var (r, c, aSeul, wSeul) = g[0];
            var faits = new List<string>();
            if (aSeul.Count > 0) faits.Add($"{N(aSeul.Count, "arrêt brutal", "arrêts brutaux")} ({(aSeul.Count == 1 ? "le" : "le premier le")} {D(aSeul[0])}, {Jours((aSeul[0] - c.Quand).TotalDays)} après)");
            if (wSeul.Count > 0) faits.Add($"{N(wSeul.Count, "erreur matérielle", "erreurs matérielles")} (la première le {D(wSeul[0])})");
            sortie.Add((r, new RepairFinding("STABILITE", "après un changement", string.Join(", ", faits), "aucun incident",
                RepairStatus.Suspicious,
                $"dans la semaine qui a suivi {c.Libelle} ({D(c.Quand)}) : {string.Join(" et ", faits)}. C'est un rapprochement "
                + "de dates, pas une preuve, mais c'est une piste : revenir au réglage d'avant, puis regarder si les "
                + "incidents s'arrêtent.",
                RepairAction.Recommandation)));
        }
        return sortie.OrderBy(x => x.Rang).Select(x => x.F).ToList();
    }
}
