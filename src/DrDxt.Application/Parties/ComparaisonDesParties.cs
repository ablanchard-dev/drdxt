namespace DrDxt.Application.Parties;

public enum VerdictEnJeu { PasEncoreJuge, PasDeDifference, Mieux, Pire }

/// <summary>
/// Un réglage de la carte ne reste que s'il ne coûte rien en jeu. Les
/// parties relevées du même jeu, avant et après le réglage, se comparent sur leur 1 % le plus lent (la médiane de la
/// partie) : c'est lui qu'un réglage d'alimentation abîmerait d'abord.
///
/// Même règle que la preuve du repos : séparation nette ou rien. Pire = toutes les parties d'après sous toutes celles
/// d'avant ; mieux = toutes au-dessus. Moins de trois parties d'un côté : pas encore jugé. Un autre jeu ne compte pas.
/// </summary>
/// <summary>Ce qu'un changement a fait en jeu, avec les chiffres qui fondent le verdict.</summary>
/// <param name="EcartBornesPercent">L'écart dans le pire cas des bornes (après le plus bas
/// contre avant le plus haut pour « mieux », l'inverse pour « pire ») ; null hors verdict tranché. Un gain se dit « au moins »
/// cela : 3 parties contre 3 tombent nettement séparées une fois sur 20 par hasard.</param>
public sealed record BilanDuChangement(VerdictEnJeu Verdict, string? Jeu, int Avant, int Apres,
    double? Low1Avant, double? Low1Apres, double? FpsAvant, double? FpsApres, double? EcartBornesPercent = null);

public static class ComparaisonDesParties
{
    public const int PartiesMinimum = 3;

    /// <summary>Le bilan d'un changement sur le jeu le plus joué depuis (sinon avant) : même verdict que
    /// <see cref="Juger"/>, plus les médianes de chaque côté pour que l'écran les dise.</summary>
    /// <param name="jusqua">Le changement suivant. Sans borne, l'après d'un changement
    /// contenait les parties d'après le suivant : son écart portait déjà le gain de l'autre, et le total les comptait deux
    /// fois. null = aucun changement depuis.</param>
    /// <param name="apartirDe">Le changement précédent. Sans borne, l'avant remontait
    /// avant lui : la médiane d'avant mélangeait les deux époques et l'écart portait encore le gain du précédent.</param>
    public static BilanDuChangement Bilan(IReadOnlyList<PartieRelevee> parties, DateTimeOffset quand, DateTimeOffset? depuis = null,
        DateTimeOffset? jusqua = null, DateTimeOffset? apartirDe = null)
    {
        // Une partie jouée en essai en alternance a un levier dans un état qui n'est pas celui du jour.
        parties = parties.Where(p => p.Essai is null).ToList();
        if (jusqua is { } fin) parties = parties.Where(p => p.Debut < quand || p.Fin <= fin).ToList();
        if (apartirDe is { } debut) parties = parties.Where(p => p.Debut >= quand || p.Debut >= debut).ToList();
        var finAvant = depuis is { } d && d < quand ? d : quand;
        string? Plus(IEnumerable<PartieRelevee> ps) => ps.GroupBy(p => p.Jeu, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        // Le jeu joué des deux côtés d'abord ; sinon un autre jeu joué après rendrait
        // « aucune partie avant » alors que le jeu habituel en a.
        var jeu = parties.Where(p => p.Fin <= finAvant).Select(p => p.Jeu)
                      .Intersect(parties.Where(p => p.Debut >= quand).Select(p => p.Jeu), StringComparer.OrdinalIgnoreCase)
                      .OrderByDescending(j => parties.Count(p => string.Equals(p.Jeu, j, StringComparison.OrdinalIgnoreCase)))
                      .FirstOrDefault()
                  ?? Plus(parties.Where(p => p.Debut >= quand)) ?? Plus(parties.Where(p => p.Fin <= finAvant));
        if (jeu is null) return new(VerdictEnJeu.PasEncoreJuge, null, 0, 0, null, null, null, null);

        var dujeu = parties.Where(p => string.Equals(p.Jeu, jeu, StringComparison.OrdinalIgnoreCase)).ToList();
        var avant = dujeu.Where(p => p.Fin <= finAvant).ToList();
        var apres = dujeu.Where(p => p.Debut >= quand).ToList();
        double? Med(IEnumerable<double> v) { var t = v.OrderBy(x => x).ToArray(); return t.Length == 0 ? null : t.Length % 2 == 1 ? t[t.Length / 2] : (t[t.Length / 2 - 1] + t[t.Length / 2]) / 2; }
        var verdict = avant.Count < PartiesMinimum || apres.Count < PartiesMinimum ? VerdictEnJeu.PasEncoreJuge
            : apres.Max(p => p.Low1Median) < avant.Min(p => p.Low1Median) ? VerdictEnJeu.Pire
            : apres.Min(p => p.Low1Median) > avant.Max(p => p.Low1Median) ? VerdictEnJeu.Mieux
            : VerdictEnJeu.PasDeDifference;
        double? bornes = verdict switch
        {
            VerdictEnJeu.Mieux when avant.Max(p => p.Low1Median) > 0 => (apres.Min(p => p.Low1Median) / avant.Max(p => p.Low1Median) - 1) * 100,
            VerdictEnJeu.Pire when avant.Min(p => p.Low1Median) > 0 => (apres.Max(p => p.Low1Median) / avant.Min(p => p.Low1Median) - 1) * 100,
            _ => null,
        };
        return new(verdict, jeu, avant.Count, apres.Count,
            Med(avant.Select(p => p.Low1Median)), Med(apres.Select(p => p.Low1Median)),
            Med(avant.Select(p => p.FpsMedian)), Med(apres.Select(p => p.FpsMedian)), bornes);
    }

    public static VerdictEnJeu Juger(IEnumerable<PartieRelevee> parties, DateTimeOffset reglage, string jeu)
    {
        var dujeu = parties.Where(p => p.Essai is null && string.Equals(p.Jeu, jeu, StringComparison.OrdinalIgnoreCase)).ToList();
        var avant = dujeu.Where(p => p.Fin <= reglage).Select(p => p.Low1Median).ToList();
        var apres = dujeu.Where(p => p.Debut >= reglage).Select(p => p.Low1Median).ToList();
        if (avant.Count < PartiesMinimum || apres.Count < PartiesMinimum) return VerdictEnJeu.PasEncoreJuge;
        return apres.Max() < avant.Min() ? VerdictEnJeu.Pire
            : apres.Min() > avant.Max() ? VerdictEnJeu.Mieux
            : VerdictEnJeu.PasDeDifference;
    }
}
