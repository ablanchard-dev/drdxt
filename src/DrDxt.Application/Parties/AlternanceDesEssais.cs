using DrDxt.Core;
namespace DrDxt.Application.Parties;

/// <summary>
/// 07/10/2026 (chantier J4) : un levier à chaud jugé sur des parties A/B, sans geste à chaque partie. Le joueur
/// allume l'alternance pour UN levier ; DrDXT le pose une partie sur deux. Chaque partie relevée porte l'état de l'essai
/// (<see cref="PartieRelevee.Essai"/> = « id:on » ou « id:off ») ; le juge compare les parties « avec » et « sans » du
/// même jeu, à la même règle que les changements (séparation nette, <see cref="ComparaisonDesParties.PartiesMinimum"/>
/// de chaque côté). Tranché, l'alternance s'arrête sur le meilleur état ; « pas de différence » rend l'état d'origine.
/// Ce type ne touche rien : il dit l'état de la prochaine partie, l'appelant pose ou rend le levier.
/// </summary>
public static class AlternanceDesEssais
{
    public static string Marque(string levierId, bool pose) => levierId + (pose ? ":on" : ":off");

    /// <summary>Le verdict des parties « avec » contre « sans » ce levier, sur le jeu le plus joué des deux côtés.</summary>
    public static VerdictEnJeu Juger(IReadOnlyList<PartieRelevee> parties, string levierId, DateTimeOffset? depuis = null)
    {
        // Revue F-33 : les parties d'un essai précédent du même levier ne comptent pas.
        if (depuis is { } d) parties = parties.Where(p => p.Debut >= d).ToList();
        var avec = parties.Where(p => p.Essai == Marque(levierId, true)).ToList();
        var sans = parties.Where(p => p.Essai == Marque(levierId, false)).ToList();
        var jeu = avec.Select(p => p.Jeu).Intersect(sans.Select(p => p.Jeu), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(j => parties.Count(p => string.Equals(p.Jeu, j, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();
        if (jeu is null) return VerdictEnJeu.PasEncoreJuge;
        var pAvec = avec.Where(p => string.Equals(p.Jeu, jeu, StringComparison.OrdinalIgnoreCase)).ToList();
        var pSans = sans.Where(p => string.Equals(p.Jeu, jeu, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pAvec.Count < ComparaisonDesParties.PartiesMinimum || pSans.Count < ComparaisonDesParties.PartiesMinimum)
            return VerdictEnJeu.PasEncoreJuge;
        // B4 (10/10/2026) : un levier de latence se juge sur la latence d'affichage (plus bas = mieux) quand toutes les
        // parties la portent ; sinon, comme les autres, sur les 1 % bas (plus haut = mieux).
        if (JugeSurLaLatence(levierId) && pAvec.Concat(pSans).All(p => p.LatenceAffichageMedianMs is not null))
        {
            var la = pAvec.Select(p => p.LatenceAffichageMedianMs!.Value).ToList();
            var ls = pSans.Select(p => p.LatenceAffichageMedianMs!.Value).ToList();
            return la.Max() < ls.Min() ? VerdictEnJeu.Mieux : la.Min() > ls.Max() ? VerdictEnJeu.Pire : VerdictEnJeu.PasDeDifference;
        }
        var a = pAvec.Select(p => p.Low1Median).ToList();
        var s = pSans.Select(p => p.Low1Median).ToList();
        return a.Min() > s.Max() ? VerdictEnJeu.Mieux : a.Max() < s.Min() ? VerdictEnJeu.Pire : VerdictEnJeu.PasDeDifference;
    }

    /// <summary>B4 : les leviers de la famille « latence » du catalogue (file de rendu, VRR + Vsync + plafond) se jugent
    /// sur la latence d'affichage, pas sur les images.</summary>
    public static bool JugeSurLaLatence(string levierId) =>
        CatalogueDeCandidats.Decrire(levierId) is { Family: CandidateFamily.Latency };

    /// <summary>Ce que la prochaine partie doit avoir : posé ou non, et si l'alternance est finie. Pas encore tranché :
    /// l'inverse de la dernière partie de l'essai (la première partie après l'allumage l'a avec). Tranché : « mieux »
    /// garde le levier, sinon l'état d'origine revient.</summary>
    public static (bool Pose, bool Fini, VerdictEnJeu Verdict) Suivante(IReadOnlyList<PartieRelevee> parties, string levierId,
        bool poseALOrigine, DateTimeOffset? depuis = null)
    {
        if (depuis is { } d) parties = parties.Where(p => p.Debut >= d).ToList();
        var verdict = Juger(parties, levierId);
        if (verdict != VerdictEnJeu.PasEncoreJuge)
            return (verdict == VerdictEnJeu.Mieux || (verdict == VerdictEnJeu.PasDeDifference && poseALOrigine), true, verdict);
        var derniere = parties.Where(p => p.Essai is { } e && e.StartsWith(levierId + ":", StringComparison.Ordinal))
            .OrderByDescending(p => p.Fin).FirstOrDefault();
        return (derniere is null || derniere.Essai == Marque(levierId, false), false, verdict);
    }
}
