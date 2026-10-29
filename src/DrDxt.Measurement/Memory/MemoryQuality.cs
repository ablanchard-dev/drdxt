namespace DrDxt.Measurement.Memory;

/// <summary>Le verdict d'acceptation d'une mesure. Rejetee, elle n'affiche aucun chiffre.</summary>
public sealed record MemoryQualityVerdict(bool Accepted, string? RejectionId);

/// <summary>
/// Les controles qui peuvent dire non a une mesure memoire : un nombre qui ne peut pas etre faux ne prouve rien.
/// Chaque controle porte un identifiant, parce qu'un rejet doit designer ce qui a echoue.
/// </summary>
public static class MemoryQuality
{
    /// <summary>
    /// Plancher de latence en dessous duquel on ne mesure plus la DRAM. Choix produit : un acces aleatoire en DDR4
    /// comme en DDR5 coute des dizaines de ns (66 ns mesures sur la machine de reference le 06/09/2026), un L3 environ 15 ns ;
    /// 40 ns separe les deux avec de la marge des deux cotes.
    /// </summary>
    public const double DramLatencyFloorNs = 40.0;

    /// <summary>
    /// Dispersion maximale acceptee, en pourcent de la moyenne. Choix produit cadre par deux mesures : 1,83 % (debit)
    /// et 3,45 % (latence) au repos, 9,9 % de bruit in-game mesure le 01/09. 5 % laisse passer une machine normale et refuse
    /// une machine occupee.
    /// </summary>
    public const double MaxSigmaPct = 5.0;

    /// <summary>
    /// Recul tolere d'un palier a l'autre, en fraction du palier precedent. Choix produit : deux paliers voisins
    /// peuvent se croiser de quelques pourcents par bruit ; au-dela, la courbe ne decrit plus une hierarchie de caches.
    /// </summary>
    public const double StaircaseTolerance = 0.10;

    /// <summary>
    /// Recul minimal, en ns, pour qu'un palier compte comme un vrai recul. Mesure du 07/09/2026 : un creux de 0,2 ns
    /// entre 32 et 48 Ko faisait rejeter en Q-02 une mesure valide (chute de debit de 89 % par ailleurs). A l'echelle
    /// du L1, le chronometre et la boucle pesent plus que la memoire : un refus doit etre grand en pourcentage ET en
    /// valeur absolue.
    /// </summary>
    public const double StaircaseMinDropNs = 0.5;

    public static MemoryQualityVerdict Check(MemoryMeasurement m, double theoreticalPeakGBs)
    {
        // Q-01 : un debit au-dela du pic theorique est impossible ; c'est le buffer qui tient dans le cache, ou le JIT
        // qui a supprime la boucle. Un plafond nul veut dire « frequence illisible » : sans reference, Q-01 se tait au
        // lieu de rejeter toute mesure d'une machine saine.
        if (theoreticalPeakGBs > 0 && m.BandwidthGBs > theoreticalPeakGBs)
            return new MemoryQualityVerdict(false, "Q-01");

        // Q-04 : une latence trop basse dit « on n'a pas atteint la memoire ». Voir DramLatencyFloorNs.
        if (m.LatencyNs < DramLatencyFloorNs)
            return new MemoryQualityVerdict(false, "Q-04");

        // Q-03 : une mesure dispersee decrit l'agitation de la machine. Pas stockee : une reference prise sur une
        // machine chargee resterait fausse pour toujours.
        if (m.BandwidthSigmaPct > MaxSigmaPct || m.LatencySigmaPct > MaxSigmaPct)
            return new MemoryQualityVerdict(false, "Q-03");

        // Q-02 : l'escalier ne redescend pas, chaque palier deborde d'un cache de plus.
        var paliers = m.Staircase.OrderBy(p => p.WindowBytes).ToList();
        for (int i = 1; i < paliers.Count; i++)
        {
            var recul = paliers[i - 1].LatencyNs - paliers[i].LatencyNs;
            var reculRelatif = paliers[i - 1].LatencyNs * StaircaseTolerance;

            if (recul > reculRelatif && recul > StaircaseMinDropNs)
                return new MemoryQualityVerdict(false, "Q-02");
        }

        // Q-05 : un escalier plat n'est pas une hierarchie. Une boucle de fenetre cassee rendrait 66 ns partout, ce que
        // ni Q-02 ni Q-04 ne refusent. Le controle ne nomme aucun niveau : la latence doit sauter au moins une fois
        // (cinq sauts sur la machine de reference, L3->DRAM a 1,69x).
        if (paliers.Count >= 2 && CacheStaircase.FindJumps(paliers).Count == 0)
            return new MemoryQualityVerdict(false, "Q-05");

        return new MemoryQualityVerdict(true, null);
    }
}
