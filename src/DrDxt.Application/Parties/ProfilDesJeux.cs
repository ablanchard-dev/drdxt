using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>Ce que les parties d'un jeu disent de lui sur cette machine.</summary>
public sealed record ProfilDeJeu(string Jeu, int Parties, double FpsMedian, double Low1Median, BottleneckState Goulot, DateTimeOffset Derniere);

/// <summary>
/// Le profil par jeu, tiré des parties relevées. Une partie isolée ne fait pas un profil (deux
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
                return new ProfilDeJeu(g.Key, g.Count(), Mediane(g.Select(p => p.FpsMedian)), Mediane(g.Select(p => p.Low1Median)),
                    GoulotDominant(g), g.Max(p => p.Fin));
            })
            .OrderByDescending(p => p.Parties)
            .ToList();

    /// <summary>Le goulot le plus fréquent parmi les parties mesurées : une partie trop courte (UnknownPartial) ne l'emporte
    /// jamais sur des parties mesurées.</summary>
    public static BottleneckState GoulotDominant(IEnumerable<PartieRelevee> parties)
    {
        var mesures = parties.Where(p => p.Goulot != BottleneckState.UnknownPartial).ToList();
        return mesures.Count == 0 ? BottleneckState.UnknownPartial
            : mesures.GroupBy(p => p.Goulot).OrderByDescending(x => x.Count()).First().Key;
    }

    private static double Mediane(IEnumerable<double> valeurs)
    {
        var v = valeurs.OrderBy(x => x).ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}
