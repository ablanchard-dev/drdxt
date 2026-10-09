namespace DrDxt.Measurement.Memory;

/// <summary>
/// L'escalier de cache : la latence en fonction de la taille de la zone parcourue.
///
/// C'est la mesure la plus ancienne du domaine et la plus difficile a truquer. Tant que la zone
/// tient dans un niveau de cache, la latence est plate ; des qu'elle deborde, elle saute. Les
/// sauts DONNENT donc les frontieres -- L1, L2, L3, puis la DRAM -- sans qu'on ait rien a
/// demander au systeme.
///
/// Ce que ca sert dans ce produit : un escalier qui a la bonne forme prouve que l'instrument
/// mesure bien une hierarchie memoire, et le dernier palier prouve que la mesure de latence
/// atteint la DRAM au lieu de rester dans le cache -- le bug paye le 06/09/2026.
/// </summary>
public static class CacheStaircase
{
    /// <summary>
    /// Facteur a partir duquel un ecart entre deux paliers est un saut de niveau et non du bruit.
    /// **Choix produit** : un niveau de cache coute typiquement trois a cinq fois le precedent,
    /// quand deux mesures voisines d'un meme niveau ne s'ecartent que de quelques pourcents.
    /// 1,5 passe entre les deux sans etre serre.
    /// </summary>
    public const double JumpFactor = 1.5;

    /// <summary>
    /// Les SAUTS de la courbe, en octets : pour chacun, la derniere fenetre avant le saut.
    /// Une courbe plate rend une liste vide -- aucun saut invente.
    ///
    /// Ce que ce n'est pas, mesure le 06/09/2026 sur la machine de reference : un saut n'est
    /// Pas une frontiere de cache. La sonde a rendu 5 sauts la ou il n'y a que 3 frontieres
    /// (L1->L2, L2->L3, L3->DRAM) : la latence croit aussi A L'INTERIEUR d'un niveau, de 1,56x
    /// entre 256 Ko et 2 Mo puis encore autant entre 16 et 32 Mo, sans qu'aucun cache soit
    /// franchi. Et le seuil ne peut pas les separer : monter a 2,0 tuerait le vrai passage
    /// L3->DRAM, mesure a 1,69x. **Cette fonction ne nomme donc aucun niveau de cache**, et
    /// personne n'a le droit de lui faire dire « ton L2 fait tant ».
    /// </summary>
    public static IReadOnlyList<long> FindJumps(IReadOnlyList<StaircasePoint> curve)
    {
        var points = curve.OrderBy(p => p.WindowBytes).ToList();
        var knees = new List<long>();

        for (int i = 1; i < points.Count; i++)
        {
            if (points[i - 1].LatencyNs > 0 &&
                points[i].LatencyNs >= points[i - 1].LatencyNs * JumpFactor)
            {
                knees.Add(points[i - 1].WindowBytes);
            }
        }

        return knees;
    }
}
