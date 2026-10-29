namespace DrDxt.Measurement.Memory;

/// <summary>
/// L'escalier de cache : la latence en fonction de la taille de la zone parcourue, plate tant qu'elle tient dans un
/// niveau, en saut des qu'elle deborde. Un escalier de bonne forme prouve que l'instrument mesure une hierarchie
/// memoire, et le dernier palier que la latence atteint la DRAM.
/// </summary>
public static class CacheStaircase
{
    /// <summary>Facteur a partir duquel un ecart entre deux paliers est un saut, pas du bruit. **Choix produit** : un
    /// niveau coute trois a cinq fois le precedent, deux mesures d'un meme niveau quelques pourcents.</summary>
    public const double JumpFactor = 1.5;

    /// <summary>
    /// Les sauts de la courbe, en octets : pour chacun, la derniere fenetre avant le saut.
    /// Une courbe plate rend une liste vide. Un saut n'est pas une frontiere de cache (mesure le 06/09/2026 sur la
    /// machine de reference) : 5 sauts pour 3 frontieres, la latence croissant aussi dans un niveau (1,56x entre 256 Ko et
    /// 2 Mo, autant entre 16 et 32 Mo), et un seuil a 2,0 tuerait le vrai passage L3->DRAM, mesure a 1,69x. Aucun niveau
    /// de cache n'est donc nomme.
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
