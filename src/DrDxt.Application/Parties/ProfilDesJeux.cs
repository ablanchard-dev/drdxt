using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>Ce que les parties d'un jeu disent de lui sur cette machine.</summary>
public sealed record ProfilDeJeu(string Jeu, int Parties, double FpsMedian, double Low1Median, BottleneckState Goulot, DateTimeOffset Derniere);

/// <summary>
/// 04/10/2026 (plan 3b.4) : le profil par jeu, tiré des parties relevées. Une partie isolée ne fait pas un profil (deux
/// au moins) ; les médianes résistent à une partie ratée ; « inconnu » ne l'emporte jamais au nombre sur un goulot mesuré
/// (même règle que le résumé d'une partie). Les réglages lus dans le jeu restent dans leur domaine (Call of Duty seul lu
/// aujourd'hui, ReglagesCod) : lire ceux d'un jeu auquel personne ne joue sur cette machine serait du travail sans effet.
/// </summary>
public static class ProfilDesJeux
{
    public const int PartiesMinimum = 2;

    public static IReadOnlyList<ProfilDeJeu> Construire(IReadOnlyList<PartieRelevee> parties)
        => parties.GroupBy(p => p.Jeu, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= PartiesMinimum)
            .Select(g =>
            {
                var mesures = g.Where(p => p.Goulot != BottleneckState.UnknownPartial).ToList();
                var goulot = mesures.Count == 0 ? BottleneckState.UnknownPartial
                    : mesures.GroupBy(p => p.Goulot).OrderByDescending(x => x.Count()).First().Key;
                return new ProfilDeJeu(g.Key, g.Count(), Mediane(g.Select(p => p.FpsMedian)), Mediane(g.Select(p => p.Low1Median)),
                    goulot, g.Max(p => p.Fin));
            })
            .OrderByDescending(p => p.Parties)
            .ToList();

    private static double Mediane(IEnumerable<double> valeurs)
    {
        var v = valeurs.OrderBy(x => x).ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}
