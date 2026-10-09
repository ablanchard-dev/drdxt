namespace DrDxt.Core;

/// <summary>
/// Le facteur de Student bilateral a 95 %, une seule fois pour tout le produit.
///
/// 26/09/2026 : trois copies existaient et divergeaient (l'une s'arretait a 9 degres de liberte) ;
/// toutes retombaient sur 1,96 au-dela de leur table, soit une marge jusqu'a 13,7 % trop etroite.
/// Au-dela de 20 degres, la derniere valeur de la table est gardee : t decroit avec les degres de
/// liberte, donc la marge reste au moins aussi large que la vraie (au plus 6 % de trop), jamais plus
/// etroite. Valeurs : t(0,975 ; ddl), verifiees contre scipy.stats.t.ppf.
/// </summary>
public static class Student
{
    private static readonly double[] Table =
    {
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,  // ddl 1-10
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,   // ddl 11-20
    };

    /// <summary><c>+∞</c> sans degre de liberte : aucune marge n'est calculable.</summary>
    public static double T95(int ddl) =>
        ddl <= 0 ? double.PositiveInfinity : Table[Math.Min(ddl, Table.Length) - 1];
}
