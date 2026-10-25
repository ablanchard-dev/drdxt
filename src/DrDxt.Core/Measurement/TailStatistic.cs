namespace DrDxt.Core;

/// <summary>
/// Quand un centile de queue cesse d'etre un centile de queue.
///
/// En lisant une vraie capture. Huit frames prises sur un terminal en
/// dix secondes ont rendu `animationError_p95 = 1186,95 ms`. Le chiffre est calcule correctement
/// et il ne veut rien dire :
///
///   · a huit echantillons, le 95e centile est l'avant-dernier point, c'est-a-dire le maximum, a
///     un point pres. Il ne decrit plus « les 5 % les plus lents », il decrit le pire ;
///   · et 1 187 ms, sur une capture a 0,8 image par seconde, n'est que l'intervalle entre deux
///     images. La grandeur recopie la cadence au lieu de mesurer un ecart a la cadence.
///
/// Personne n'aurait vu l'erreur : le nombre est plausible, l'unite est juste, aucune exception
/// n'est levee. C'est exactement la forme de defaut que ce produit existe pour ne pas commettre,
/// et il a suffi de regarder une sortie reelle pour la trouver.
/// </summary>
public static class TailStatistic
{
    /// <summary>
    /// Sous ce nombre d'echantillons, un p95 se confond avec le maximum.
    ///
    /// Ce seuil n'est pas un reglage de prudence, c'est de l'arithmetique : le 95e centile ne
    /// commence a ecarter au moins un point du maximum qu'a partir de vingt echantillons
    /// (1/20 = 5 %). En dessous, appeler le resultat « p95 » nomme une robustesse qui n'existe pas.
    /// </summary>
    public const int EchantillonsMinimumPourUnP95 = 20;

    /// <summary>Un p95 calcule sur <paramref name="echantillons"/> points merite-t-il son nom ?</summary>
    public static bool P95EstInterpretable(int echantillons) =>
        echantillons >= EchantillonsMinimumPourUnP95;

    /// <summary>Pourquoi ce p95 ne veut rien dire, ou <c>null</c> quand il en vaut un.
    ///
    /// Rend une phrase plutot qu'un booleen la ou c'est affiche : « p95 non interpretable »
    /// sans dire pourquoi se lit comme une panne de l'outil, alors que c'est une propriete de la
    /// capture.</summary>
    public static string? PourquoiPasInterpretable(int echantillons) =>
        P95EstInterpretable(echantillons)
            ? null
            : Docteur.T("TS.PasInterpretable", echantillons, EchantillonsMinimumPourUnP95);
}
