namespace DrDxt.Application.Parties;

/// <summary>Les sept derniers jours, comptés ; les FPS comparés à la semaine d'avant quand les deux en ont.</summary>
public sealed record Bilan(int Parties, double? FpsMedian, double? FpsMedianSemainePrecedente, int Changements, int Arrets, int Erreurs);

/// <summary>
/// Le bilan de la semaine, en une ligne en tête de l'historique. Les FPS ne se comparent qu'au
/// même jeu (le plus joué de la semaine) et seulement s'il y a des parties des deux côtés ; sinon la comparaison est tue.
/// Pur : tout vient de l'appelant.
/// </summary>
public static class BilanDeLaSemaine
{
    public static Bilan Construire(IReadOnlyList<PartieRelevee> parties, IReadOnlyList<Changement> changements,
        IReadOnlyList<DateTimeOffset> arrets, IReadOnlyList<DateTimeOffset> erreurs, DateTimeOffset maintenant)
    {
        var debut = maintenant.AddDays(-7);
        var avant = maintenant.AddDays(-14);
        var semaine = parties.Where(p => p.Fin > debut && p.Fin <= maintenant).ToList();
        var jeu = semaine.GroupBy(p => p.Jeu, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        double? Med(IEnumerable<PartieRelevee> ps)
        {
            var v = ps.Where(p => string.Equals(p.Jeu, jeu, StringComparison.OrdinalIgnoreCase)).Select(p => p.FpsMedian).OrderBy(x => x).ToArray();
            return v.Length == 0 ? null : v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
        }
        return new Bilan(semaine.Count, Med(semaine), Med(parties.Where(p => p.Fin > avant && p.Fin <= debut)),
            changements.Count(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true && c.Quand > debut && c.Quand <= maintenant),
            arrets.Count(a => a > debut && a <= maintenant), erreurs.Count(e => e > debut && e <= maintenant));
    }
}
