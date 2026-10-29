namespace DrDxt.Application.Parties;

// NonConcluant : le test à blanc de l'essai en alternance a vu un écart entre deux groupes au même réglage.
// PasComparable : les parties d'avant et d'après n'ont pas le même estimateur du 1 % low (PartieRelevee.Low1SurToutesLesImages).
// Ajouté en dernier : un verdict figé déjà écrit garde sa valeur.
/// <summary>Écrit par son nom dans les verdicts figés : un entier changerait de sens au premier verdict ajouté avant lui,
/// et un « Mieux » figé pourrait se relire « Pire ». Un ancien entier se relit encore.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<VerdictEnJeu>))]
public enum VerdictEnJeu { PasEncoreJuge, PasDeDifference, Mieux, Pire, NonConcluant, PasComparable }

// La règle de ComparaisonDesParties : un réglage de la carte ne reste que s'il ne coûte rien en jeu. Les parties relevées
// du même jeu, avant et après le réglage, se comparent sur leur 1 % le plus lent (la médiane de la partie) : c'est lui
// qu'un réglage d'alimentation abîmerait d'abord. Même règle que la preuve du repos : séparation nette ou rien. Pire =
// toutes les parties d'après sous toutes celles d'avant ; mieux = toutes au-dessus. Moins de trois parties d'un côté :
// pas encore jugé. Un autre jeu ne compte pas.

/// <summary>Ce qu'un changement a fait en jeu, avec les chiffres qui fondent le verdict.</summary>
/// <param name="EcartBornesPercent">L'écart de la pire paire (après le plus bas contre avant le plus haut), pour « mieux »
/// comme pour « pire » : la valeur la plus basse que le rapport a prise, que « au moins » compose ; null hors verdict
/// tranché. Pour une perte, la paire la plus proche de zéro ne borne rien par en dessous.</param>
/// <param name="SurLaLatence">Jugé sur la latence d'affichage (plus bas = mieux), pas sur le 1 % low : un levier de latence
/// dont les parties la portent. <paramref name="LatenceAvant"/> et <paramref name="LatenceApres"/> sont alors ses médianes.</param>
public sealed record BilanDuChangement(VerdictEnJeu Verdict, string? Jeu, int Avant, int Apres,
    double? Low1Avant, double? Low1Apres, double? FpsAvant, double? FpsApres, double? EcartBornesPercent = null,
    bool SurLaLatence = false, double? LatenceAvant = null, double? LatenceApres = null);

public static class ComparaisonDesParties
{
    public const int PartiesMinimum = 3;

    /// <summary>Le bilan d'un changement sur le jeu le plus joué depuis (sinon avant) : même verdict que
    /// <see cref="Juger"/>, plus les médianes de chaque côté pour que l'écran les dise.</summary>
    /// <param name="jusqua">Le changement suivant, qui borne l'après (sans elle, l'écart porterait déjà le gain du suivant,
    /// compté deux fois). null = aucun changement depuis.</param>
    /// <param name="apartirDe">Le changement précédent, qui borne l'avant (sans elle, la médiane d'avant mélangerait les
    /// deux époques).</param>
    /// <param name="surLaLatence">Le changement est un levier de latence : il se juge sur la latence d'affichage, sur les
    /// parties qui la portent, comme dans l'essai en alternance ; seulement si aucune ne la porte, sur le 1 % low.</param>
    public static BilanDuChangement Bilan(IReadOnlyList<PartieRelevee> parties, DateTimeOffset quand, DateTimeOffset? depuis = null,
        DateTimeOffset? jusqua = null, DateTimeOffset? apartirDe = null, bool surLaLatence = false)
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
        // Un levier de latence se juge sur ses parties qui portent la latence ; le 1 % low (et son estimateur) n'y entre pas.
        var latence = surLaLatence && avant.Concat(apres).Any(p => p.LatenceAffichageMedianMs is not null);
        if (latence)
            (avant, apres) = (avant.Where(p => p.LatenceAffichageMedianMs is not null).ToList(),
                apres.Where(p => p.LatenceAffichageMedianMs is not null).ToList());
        else if (MemeEstimateur(avant, apres) is not { } memes)
            return new(VerdictEnJeu.PasComparable, jeu, avant.Count, apres.Count, null, null, null, null);
        else (avant, apres) = memes;
        double? Med(IEnumerable<double> v) { var t = v.OrderBy(x => x).ToArray(); return t.Length == 0 ? null : t.Length % 2 == 1 ? t[t.Length / 2] : (t[t.Length / 2 - 1] + t[t.Length / 2]) / 2; }
        // Plus haut = mieux : la latence arrive en négatif.
        double Mesure(PartieRelevee p) => latence ? -p.LatenceAffichageMedianMs!.Value : p.Low1Median;
        var verdict = avant.Count < PartiesMinimum || apres.Count < PartiesMinimum ? VerdictEnJeu.PasEncoreJuge
            : apres.Max(Mesure) < avant.Min(Mesure) ? VerdictEnJeu.Pire
            : apres.Min(Mesure) > avant.Max(Mesure) ? VerdictEnJeu.Mieux
            : VerdictEnJeu.PasDeDifference;
        // L'écart des bornes est celui du 1 % low ; jugé sur la latence, ce sont ses médianes qui se disent.
        double? bornes = !latence && verdict is VerdictEnJeu.Mieux or VerdictEnJeu.Pire && avant.Max(p => p.Low1Median) > 0
            ? (apres.Min(p => p.Low1Median) / avant.Max(p => p.Low1Median) - 1) * 100
            : null;
        return new(verdict, jeu, avant.Count, apres.Count,
            Med(avant.Select(p => p.Low1Median)), Med(apres.Select(p => p.Low1Median)),
            Med(avant.Select(p => p.FpsMedian)), Med(apres.Select(p => p.FpsMedian)), bornes,
            latence, latence ? Med(avant.Select(p => p.LatenceAffichageMedianMs!.Value)) : null,
            latence ? Med(apres.Select(p => p.LatenceAffichageMedianMs!.Value)) : null);
    }

    /// <summary>Les parties de chaque côté qui ont l'estimateur du 1 % low le plus fréquent après le changement (avant, s'il
    /// n'y a pas encore d'après) ; null quand un côté n'atteint le minimum qu'en mêlant les deux estimateurs : le verdict
    /// serait celui de l'estimateur, pas celui du réglage. Une partie isolée de l'autre estimateur ne compte simplement pas.</summary>
    internal static (List<PartieRelevee> Avant, List<PartieRelevee> Apres)? MemeEstimateur(List<PartieRelevee> avant, List<PartieRelevee> apres)
    {
        var reference = apres.Count > 0 ? apres : avant;
        if (reference.Count == 0) return (avant, apres);
        var estimateur = EstimateurMajoritaire(reference);
        var a = avant.Where(p => Estimateur(p) == estimateur).ToList();
        var b = apres.Where(p => Estimateur(p) == estimateur).ToList();
        return (avant.Count >= PartiesMinimum && a.Count < PartiesMinimum) || (apres.Count >= PartiesMinimum && b.Count < PartiesMinimum)
            ? null
            : (a, b);
    }

    /// <summary>Ce qui a produit le 1 % low d'une partie : l'instrument (PresentMon ou le repli DXGI) et l'estimateur (0 la
    /// médiane des fenêtres, 1 toutes les images pauses comprises, 2 toutes les images sans les pauses de présentation).
    /// Deux parties de clés différentes ne se comparent pas.</summary>
    internal static string Estimateur(PartieRelevee p)
        => $"{p.Instrument ?? "PresentMon"}|{(!p.Low1SurToutesLesImages ? 0 : p.Low1SansPauses ? 2 : 1)}";

    /// <summary>L'estimateur du 1 % low le plus fréquent parmi ces parties (à égalité, le plus récent).</summary>
    internal static string EstimateurMajoritaire(IReadOnlyCollection<PartieRelevee> parties)
        => parties.GroupBy(Estimateur).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(p => p.Debut))
            .Select(g => g.Key).FirstOrDefault("PresentMon|2");

    /// <param name="jusqua">Le changement suivant, comme pour <see cref="Bilan"/> : les parties d'après lui portent son effet,
    /// et le retrait automatique attribuerait sa perte à celui-ci. null = aucun changement depuis.</param>
    public static VerdictEnJeu Juger(IEnumerable<PartieRelevee> parties, DateTimeOffset reglage, string jeu, DateTimeOffset? jusqua = null)
    {
        var dujeu = parties.Where(p => p.Essai is null && string.Equals(p.Jeu, jeu, StringComparison.OrdinalIgnoreCase)
                                       && (jusqua is not { } fin || p.Debut < reglage || p.Fin <= fin)).ToList();
        if (MemeEstimateur(dujeu.Where(p => p.Fin <= reglage).ToList(), dujeu.Where(p => p.Debut >= reglage).ToList()) is not { } memes)
            return VerdictEnJeu.PasComparable;
        var avant = memes.Avant.Select(p => p.Low1Median).ToList();
        var apres = memes.Apres.Select(p => p.Low1Median).ToList();
        if (avant.Count < PartiesMinimum || apres.Count < PartiesMinimum) return VerdictEnJeu.PasEncoreJuge;
        return apres.Max() < avant.Min() ? VerdictEnJeu.Pire
            : apres.Min() > avant.Max() ? VerdictEnJeu.Mieux
            : VerdictEnJeu.PasDeDifference;
    }
}
