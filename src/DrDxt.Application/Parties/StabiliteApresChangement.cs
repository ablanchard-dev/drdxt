using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>
/// Un changement (réglage du BIOS, levier, bascule) suivi d'un arrêt brutal ou d'une erreur
/// matérielle dans la semaine est la première piste ; le docteur rapprochait déjà les incidents des pilotes installés,
/// pas des réglages. Une semaine sans incident après un changement se dit aussi : c'est ce qui permet de le garder.
/// Pur : le journal des changements, les dates d'arrêts et d'erreurs, et l'heure, viennent de l'appelant.
/// </summary>
public static class StabiliteApresChangement
{
    public static readonly TimeSpan Surveillance = TimeSpan.FromDays(7);

    /// <summary>Un journal d'arrêts ou d'erreurs illisible, et des changements récents à
    /// suivre : « non lu ». Sans changement récent, rien à rapprocher, rien à dire.</summary>
    public static IEnumerable<RepairFinding> NonLu(IReadOnlyList<Changement> changements, DateTimeOffset maintenant)
        => changements.Any(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true && maintenant - c.Quand <= TimeSpan.FromDays(30))
            ? [new RepairFinding("STABILITE", "après un changement", null, null, RepairStatus.Unknown,
                Docteur.T("SA.NonLu"))]
            : [];

    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Changement> changements, IReadOnlyList<DateTimeOffset> arrets,
        IReadOnlyList<DateTimeOffset> erreursMaterielles, DateTimeOffset maintenant)
    {
        var format = Docteur.T("SA.Date");
        string D(DateTimeOffset d) => d.LocalDateTime.ToString(format, CultureInfo.InvariantCulture);
        // Le nombre choisit sa forme, jamais « 1 arrêt(s) brutal(aux) » : en français le singulier va jusque sous 2 (« 1,5 jour »),
        // en anglais il ne vaut que pour 1 (« 0.4 days », « 1.5 days »).
        static string Jours(double j)
        {
            var arrondi = Math.Round(j, 1);
            var singulier = Docteur.Langue.TwoLetterISOLanguageName == "fr" ? arrondi < 2 : arrondi == 1;
            return Docteur.T(singulier ? "SA.Jour" : "SA.Jours", arrondi.ToString("0.#", Docteur.Langue));
        }
        string Arrets(List<DateTimeOffset> a) => a.Count == 1 ? Docteur.T("SA.Arret1", D(a[0])) : Docteur.T("SA.Arrets", a.Count, D(a[0]));
        string Erreurs(List<DateTimeOffset> w) => w.Count == 1 ? Docteur.T("SA.Erreur1", D(w[0])) : Docteur.T("SA.Erreurs", w.Count, D(w[0]));

        // Chaque constat garde le rang de son changement : la sortie suit l'ordre des changements.
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
                sortie.Add((rang, new RepairFinding("STABILITE", "après un changement", Docteur.T("SA.PourLInstant"), Docteur.T("SA.AucunIncident"),
                    RepairStatus.Observed,
                    Docteur.T("SA.Depuis", c.Libelle, D(c.Quand), Jours((maintenant - c.Quand).TotalDays), Surveillance.TotalDays))));
        }

        // Les changements qui partagent au moins un incident se disent ensemble, de proche en
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
                if (a.Count > 0) faitsCommuns.Add(Arrets(a));
                if (w.Count > 0) faitsCommuns.Add(Erreurs(w));
                var liste = string.Join(Docteur.T("SA.Separateur"), g.Select(x => $"{x.C.Libelle} ({D(x.C.Quand)})"));
                sortie.Add((g.Min(x => x.Rang), new RepairFinding("STABILITE", "après un changement", string.Join(", ", faitsCommuns), Docteur.T("SA.AucunIncident"),
                    RepairStatus.Suspicious,
                    Docteur.T("SA.Groupe", g.Count, liste, string.Join(Docteur.T("SA.Et"), faitsCommuns)),
                    RepairAction.Recommandation,
                    Remede: Docteur.T("SA.Groupe.Remede", g.MaxBy(x => x.C.Quand).C.Libelle))));
                continue;
            }
            var (r, c, aSeul, wSeul) = g[0];
            var faits = new List<string>();
            if (aSeul.Count > 0)
                faits.Add(aSeul.Count == 1
                    ? Docteur.T("SA.Arret1Apres", D(aSeul[0]), Jours((aSeul[0] - c.Quand).TotalDays))
                    : Docteur.T("SA.ArretsApres", aSeul.Count, D(aSeul[0]), Jours((aSeul[0] - c.Quand).TotalDays)));
            if (wSeul.Count > 0) faits.Add(Erreurs(wSeul));
            sortie.Add((r, new RepairFinding("STABILITE", "après un changement", string.Join(", ", faits), Docteur.T("SA.AucunIncident"),
                RepairStatus.Suspicious,
                Docteur.T("SA.Seul", c.Libelle, D(c.Quand), string.Join(Docteur.T("SA.Et"), faits)),
                RepairAction.Recommandation,
                Remede: Docteur.T("SA.Seul.Remede", GesteDeRetour(c.Libelle)))));
        }
        return sortie.OrderBy(x => x.Rang).Select(x => x.F).ToList();
    }

    // Le libellé d'un réglage du BIOS porte l'avant et l'après (Changement.Bios, dans les deux langues) : le geste dit la
    // valeur à remettre. Tout autre changement se nomme tel quel.
    private static readonly System.Text.RegularExpressions.Regex ReglageBios =
        new("« (?<avant>.+) » passé à « (?<nouveau>.+) »|\"(?<avant>.+)\" changed to \"(?<nouveau>.+)\"");

    internal static string GesteDeRetour(string libelle)
        => ReglageBios.Match(libelle) is { Success: true } m
            ? Docteur.T("SA.Retour.Bios", m.Groups["avant"].Value, m.Groups["nouveau"].Value)
            : Docteur.T("SA.Retour.Autre", libelle);
}
