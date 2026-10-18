using DrDxt.Core;
namespace DrDxt.Application.Parties;

/// <summary>
/// Un levier à chaud jugé sur des parties A/B, sans geste à chaque partie. Le joueur
/// allume l'alternance pour un seul levier ; DrDXT le pose en blocs A-B-B-A. Chaque partie relevée porte l'état de l'essai
/// (<see cref="PartieRelevee.Essai"/> = « id:on » ou « id:off ») ; le juge compare les parties « avec » et « sans » du
/// même jeu, à la même règle que les changements (séparation nette, <see cref="ComparaisonDesParties.PartiesMinimum"/>
/// de chaque côté). Tranché, l'alternance s'arrête sur le meilleur état ; « pas de différence » rend l'état d'origine.
/// Ce type ne touche rien : il dit l'état de la prochaine partie, l'appelant pose ou rend le levier.
///
/// Les parties suivent des blocs A-B-B-A (avec, sans, sans, avec…) : une dérive au fil des parties (chaleur, mise à jour,
/// heure de jeu) touche autant les deux états. Et l'instrument passe un test à blanc avant tout verdict : les parties
/// « sans », coupées en deux moitiés alternées, sont jugées l'une contre l'autre à la même règle. Si cette comparaison A
/// contre A « voit » un écart, l'instrument voit du bruit : l'essai est non concluant, et aucun verdict ne le suit.
/// </summary>
public static class AlternanceDesEssais
{
    public static string Marque(string levierId, bool pose) => levierId + (pose ? ":on" : ":off");

    /// <summary>Le verdict des parties « avec » contre « sans » ce levier, sur le jeu le plus joué des deux côtés.</summary>
    public static VerdictEnJeu Juger(IReadOnlyList<PartieRelevee> parties, string levierId, DateTimeOffset? depuis = null)
    {
        // Les parties d'un essai précédent du même levier ne comptent pas.
        if (depuis is { } d) parties = parties.Where(p => p.Debut >= d).ToList();
        // Un levier de latence se juge sur la latence d'affichage (plus bas = mieux) : une partie qui ne la porte pas ne compte
        // d'aucun côté. Seulement si aucune ne la porte, sur les 1 % bas (plus haut = mieux), comme les autres, et c'est dit.
        var surLaLatence = JugeSurLaLatence(levierId) && !JugeSurLesImagesFauteDeLatence(parties, levierId);
        var mesurees = surLaLatence ? parties.Where(p => p.LatenceAffichageMedianMs is not null).ToList() : parties;
        var avec = mesurees.Where(p => p.Essai == Marque(levierId, true)).ToList();
        var sans = mesurees.Where(p => p.Essai == Marque(levierId, false)).ToList();
        var jeu = avec.Select(p => p.Jeu).Intersect(sans.Select(p => p.Jeu), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(j => parties.Count(p => string.Equals(p.Jeu, j, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();
        if (jeu is null) return VerdictEnJeu.PasEncoreJuge;
        var pAvec = avec.Where(p => string.Equals(p.Jeu, jeu, StringComparison.OrdinalIgnoreCase)).ToList();
        var pSans = sans.Where(p => string.Equals(p.Jeu, jeu, StringComparison.OrdinalIgnoreCase)).ToList();
        // Deux estimateurs du 1 % low ne se comparent pas : une partie de l'autre ne compte d'aucun côté. Jugé sur la
        // latence, le 1 % low n'entre pas dans le verdict : toutes les parties comptent.
        if (!surLaLatence)
        {
            var estimateur = ComparaisonDesParties.EstimateurMajoritaire([.. pAvec, .. pSans]);
            pAvec = pAvec.Where(p => p.Low1SurToutesLesImages == estimateur).ToList();
            pSans = pSans.Where(p => p.Low1SurToutesLesImages == estimateur).ToList();
        }
        // Le test à blanc demande deux moitiés de « sans » de PartiesMinimum chacune.
        if (pAvec.Count < ComparaisonDesParties.PartiesMinimum || pSans.Count < 2 * ComparaisonDesParties.PartiesMinimum)
            return VerdictEnJeu.PasEncoreJuge;
        double Mesure(PartieRelevee p) => surLaLatence ? -p.LatenceAffichageMedianMs!.Value : p.Low1Median;
        var sansDansLOrdre = pSans.OrderBy(p => p.Debut).Select(Mesure).ToList();
        var blanc = Separer(sansDansLOrdre.Where((_, i) => i % 2 == 0).ToList(), sansDansLOrdre.Where((_, i) => i % 2 == 1).ToList());
        if (blanc != VerdictEnJeu.PasDeDifference) return VerdictEnJeu.NonConcluant;
        return Separer(pAvec.Select(Mesure).ToList(), sansDansLOrdre);
    }

    /// <summary>Séparation nette ou rien, plus haut = mieux (la latence arrive en négatif).</summary>
    private static VerdictEnJeu Separer(IReadOnlyList<double> a, IReadOnlyList<double> b)
        => a.Min() > b.Max() ? VerdictEnJeu.Mieux : a.Max() < b.Min() ? VerdictEnJeu.Pire : VerdictEnJeu.PasDeDifference;

    /// <summary>Blocs A-B-B-A : la n-ième partie de l'essai (à partir de 0) a le levier aux rangs 0 et 3 de chaque bloc de 4.</summary>
    public static bool PoseAuRang(int rang) => (rang % 4) is 0 or 3;

    /// <summary>Les leviers de la famille « latence » du catalogue (file de rendu, VRR + Vsync + plafond) se jugent
    /// sur la latence d'affichage, pas sur les images.</summary>
    public static bool JugeSurLaLatence(string levierId) =>
        CatalogueDeCandidats.Decrire(levierId) is { Family: CandidateFamily.Latency };

    /// <summary>Un levier de latence dont aucune partie d'essai ne porte la latence : l'instrument ne la lit pas ici, les 1 %
    /// bas sont la seule mesure, et le verdict doit le dire.</summary>
    public static bool JugeSurLesImagesFauteDeLatence(IReadOnlyList<PartieRelevee> parties, string levierId)
        => JugeSurLaLatence(levierId)
           && !parties.Any(p => p.LatenceAffichageMedianMs is not null && p.Essai is { } e && e.StartsWith(levierId + ":", StringComparison.Ordinal));

    /// <summary>Ce que la prochaine partie doit avoir : posé ou non, et si l'alternance est finie. Pas encore tranché :
    /// le rang suivant des blocs A-B-B-A (la première partie après l'allumage l'a avec). Tranché : « mieux » garde le levier,
    /// sinon (pas de différence, pire, non concluant) l'état d'origine revient.</summary>
    public static (bool Pose, bool Fini, VerdictEnJeu Verdict) Suivante(IReadOnlyList<PartieRelevee> parties, string levierId,
        bool poseALOrigine, DateTimeOffset? depuis = null)
    {
        if (depuis is { } d) parties = parties.Where(p => p.Debut >= d).ToList();
        var verdict = Juger(parties, levierId);
        if (verdict != VerdictEnJeu.PasEncoreJuge)
            return (verdict == VerdictEnJeu.Mieux || (verdict is VerdictEnJeu.PasDeDifference or VerdictEnJeu.NonConcluant && poseALOrigine), true, verdict);
        var jouees = parties.Count(p => p.Essai is { } e && e.StartsWith(levierId + ":", StringComparison.Ordinal));
        return (PoseAuRang(jouees), false, verdict);
    }
}
