namespace DrDxt.Core;

/// <summary>
/// Quand un centile de queue cesse d'etre un centile de queue. Lu sur une vraie capture : huit frames d'un terminal en
/// dix secondes ont rendu `animationError_p95 = 1186,95 ms`, calcule juste et vide de sens :
///   · a huit echantillons, le 95e centile est l'avant-dernier point, le maximum a un point pres : le pire, pas « les
///     5 % les plus lents » ;
///   · 1 187 ms a 0,8 image par seconde n'est que l'intervalle entre deux images : la cadence recopiee.
/// Nombre plausible, unite juste, aucune exception : rien ne le signalerait.
/// </summary>
public static class TailStatistic
{
    /// <summary>Sous ce nombre d'echantillons, un p95 se confond avec le maximum : arithmetique, le 95e centile n'ecarte
    /// au moins un point du maximum qu'a partir de vingt (1/20 = 5 %).</summary>
    public const int EchantillonsMinimumPourUnP95 = 20;

    /// <summary>Un p95 calcule sur <paramref name="echantillons"/> points merite-t-il son nom ?</summary>
    public static bool P95EstInterpretable(int echantillons) =>
        echantillons >= EchantillonsMinimumPourUnP95;

    /// <summary>Pourquoi ce p95 ne veut rien dire, ou <c>null</c> quand il en vaut un : une phrase, sans quoi
    /// « non interpretable » se lirait comme une panne de l'outil et non une propriete de la capture.</summary>
    public static string? PourquoiPasInterpretable(int echantillons) =>
        P95EstInterpretable(echantillons)
            ? null
            : Docteur.T("TS.PasInterpretable", echantillons, EchantillonsMinimumPourUnP95);
}
