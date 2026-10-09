namespace DrDxt.Measurement.Memory;

/// <summary>
/// Un point de l'escalier de cache : la latence moyenne observee quand la zone parcourue tient
/// dans <paramref name="WindowBytes"/>. La courbe entiere est ce qui donne les frontieres L1/L2/L3
/// et DRAM -- un point seul ne dit rien.
/// </summary>
public sealed record StaircasePoint(long WindowBytes, double LatencyNs);

/// <summary>
/// Une mesure memoire, telle qu'un instrument la rend. Les sigma sont en POURCENT de la moyenne
/// (sigma/moyenne), parce que c'est sous cette forme qu'ils se comparent au plancher de bruit et
/// qu'ils repondent a la seule question qui compte : « cet ecart est-il un resultat ou du bruit ? ».
/// Objet de donnees pur : il ne mesure rien, il transporte ce qui a ete mesure.
/// </summary>
public sealed record MemoryMeasurement(
    double BandwidthGBs,
    double BandwidthSigmaPct,
    double LatencyNs,
    double LatencySigmaPct,
    IReadOnlyList<StaircasePoint> Staircase);
