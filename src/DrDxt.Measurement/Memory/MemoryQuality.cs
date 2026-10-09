namespace DrDxt.Measurement.Memory;

/// <summary>Le verdict d'acceptation d'une mesure. Rejetee, elle n'affiche aucun chiffre.</summary>
public sealed record MemoryQualityVerdict(bool Accepted, string? RejectionId);

/// <summary>
/// Les controles qui peuvent dire NON a une mesure memoire.
///
/// Sans eux, l'instrument n'est pas un instrument : c'est un generateur de nombres, et un nombre
/// qui ne peut pas etre faux ne prouve rien. Chaque controle porte un identifiant, parce qu'un
/// rejet doit designer ce qui a echoue -- « mesure invalide » n'apprend rien a personne.
///
/// Chaque controle est defini ci-dessous, avec sa justification.
/// </summary>
public static class MemoryQuality
{
    /// <summary>
    /// Plancher de latence en dessous duquel on ne mesure plus la DRAM. **Choix produit, pas une
    /// mesure** : la latence d'un acces aleatoire en DDR4 comme en DDR5 se compte en dizaines de
    /// nanosecondes (66 ns releves sur la machine de reference le 06/09/2026), quand un L3 repond
    /// autour de 15 ns. 40 ns separe les deux avec de la marge des deux cotes. Il est ici, nomme,
    /// plutot que cache dans une comparaison.
    /// </summary>
    public const double DramLatencyFloorNs = 40.0;

    /// <summary>
    /// Dispersion maximale acceptee, en pourcent de la moyenne. **Choix produit** cadre par deux
    /// mesures : l'instrument rend 1,83 % (debit) et 3,45 % (latence) sur une machine au repos
    /// (06/09/2026), et le plancher de bruit in-game du banc du 01/09 est de 9,9 %. 5 % laisse
    /// respirer une machine normale et refuse une machine occupee.
    /// </summary>
    public const double MaxSigmaPct = 5.0;

    /// <summary>
    /// Recul tolere d'un palier a l'autre, en fraction du palier precedent. **Choix produit** : la
    /// mesure bruite, deux paliers voisins peuvent se croiser de quelques pourcents sans que la
    /// hierarchie soit fausse. Au-dela, la courbe ne decrit plus une hierarchie de caches.
    /// </summary>
    public const double StaircaseTolerance = 0.10;

    /// <summary>
    /// Recul minimal, en nanosecondes, pour qu'un palier compte comme un vrai recul. **Choix
    /// produit, impose par une mesure** : le 07/09/2026, le controle positif a fait rejeter en
    /// Q-02 une mesure parfaitement valide a cause d'un creux de **0,2 ns** entre 32 et 48 Ko --
    /// alors que la mesure voyait par ailleurs une chute de debit de 89 %. A l'echelle du L1, la
    /// resolution du chronometre et le cout de la boucle pesent plus que la memoire : un ecart
    /// RELATIF y est enorme et ne veut rien dire. Un refus doit donc etre grand dans les deux
    /// sens, en pourcentage et en valeur absolue.
    /// </summary>
    public const double StaircaseMinDropNs = 0.5;

    public static MemoryQualityVerdict Check(MemoryMeasurement m, double theoreticalPeakGBs)
    {
        // Q-01 — un debit au-dela du pic theorique est PHYSIQUEMENT impossible. Quand il arrive,
        // ce n'est jamais la memoire qui est rapide : c'est le buffer qui tient dans le cache, ou
        // le JIT qui a supprime la boucle de lecture.
        //
        // Un plafond nul veut dire « la frequence n'a pas pu etre lue », pas « plafond zero ».
        // Sans le garde ci-dessous, « debit > 0 » etait toujours vrai et Q-01 rejetait toute
        // mesure -- apres avoir paye trente secondes de banc -- sur une machine parfaitement
        // saine dont on n'avait simplement pas su lire la vitesse. Un controle sans reference ne
        // conclut pas : il se tait.
        if (theoreticalPeakGBs > 0 && m.BandwidthGBs > theoreticalPeakGBs)
            return new MemoryQualityVerdict(false, "Q-01");

        // Q-04 — une latence trop basse ne dit pas « memoire rapide », elle dit « on n'a pas
        // atteint la memoire ». Voir DramLatencyFloorNs.
        if (m.LatencyNs < DramLatencyFloorNs)
            return new MemoryQualityVerdict(false, "Q-04");

        // Q-03 — une mesure dispersee decrit l'agitation de la machine, pas sa memoire. Elle n'est
        // pas stockee : une reference prise sur une machine chargee resterait fausse pour toujours.
        if (m.BandwidthSigmaPct > MaxSigmaPct || m.LatencySigmaPct > MaxSigmaPct)
            return new MemoryQualityVerdict(false, "Q-03");

        // Q-02 — l'escalier ne redescend pas : chaque palier deborde d'un cache de plus, donc
        // coute au moins aussi cher que le precedent.
        var paliers = m.Staircase.OrderBy(p => p.WindowBytes).ToList();
        for (int i = 1; i < paliers.Count; i++)
        {
            var recul = paliers[i - 1].LatencyNs - paliers[i].LatencyNs;
            var reculRelatif = paliers[i - 1].LatencyNs * StaircaseTolerance;

            if (recul > reculRelatif && recul > StaircaseMinDropNs)
                return new MemoryQualityVerdict(false, "Q-02");
        }

        // Q-05 — un escalier plat n'est pas une hierarchie. Q-02 refuse une courbe qui redescend,
        // Q-04 une latence trop basse pour etre de la DRAM ; aucune ne refuse une courbe qui ne
        // BOUGE pas. Une sonde dont la boucle de fenetre serait cassee rendrait 66 ns partout :
        // au-dessus du plancher, jamais decroissante, peu dispersee — donc acceptee, enregistree
        // comme reference, et desormais AFFICHEE. `CacheStaircase` existait pour repondre a ca et
        // n'etait appele par personne.
        //
        // Un saut n'est pas une frontiere de cache et ce controle ne nomme aucun niveau : il
        // demande seulement que la latence ait saute au moins une fois. Sur la machine de
        // reference, la courbe en montre cinq, et le vrai passage L3->DRAM vaut 1,69x.
        if (paliers.Count >= 2 && CacheStaircase.FindJumps(paliers).Count == 0)
            return new MemoryQualityVerdict(false, "Q-05");

        return new MemoryQualityVerdict(true, null);
    }
}
