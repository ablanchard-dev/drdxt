using DrDxt.Core;

namespace DrDxt.Measurement.Memory;

/// <summary>
/// Ce que la mesure apprend de la memoire, et que la lecture ne peut pas dire.
///
/// `MemoryTopologyAdvisor` deduit deja les canaux des chaines `DeviceLocator`, et il se tait quand
/// une carte les nomme mal. Ici on raisonne dans l'autre sens, par une borne : un canal a une
/// vitesse plafond, et rien ne peut la depasser. Un debit mesure au-dessus de ce plafond prouve
/// qu'il y a plusieurs canaux, quelle que soit la facon dont la carte nomme ses emplacements.
///
/// ══ ce que ce raisonnement n'a pas le droit de faire : conclure a l'envers ══
/// Un debit bas ne prouve rien du tout. Un seul fil d'execution ne sature pas forcement deux
/// canaux : le facteur limitant est alors le nombre d'acces qu'un coeur garde en vol, pas la
/// memoire. Le plancher rend donc `1` au sens de « au moins un », c'est-a-dire Unknown, et jamais
/// « simple canal ».
/// </summary>
public static class MemoryInference
{
    /// <summary>Octets par transfert d'un canal DDR : le bus fait 64 bits. Valeur de la norme.</summary>
    private const double BytesPerTransfer = 8.0;

    /// <summary>
    /// Marge sous le plafond avant de conclure. **Choix produit** : la frequence lue est arrondie,
    /// l'horloge derive, et un debit a 99,5 % du plafond theorique reste plus vraisemblablement une
    /// mesure genereuse qu'un canal de plus. On exige de depasser franchement, pas de froler.
    /// </summary>
    private const double ProofMargin = 1.02;

    /// <summary>
    /// Debit plafond d'un canal, en Go/s, a la frequence configuree. Les barrettes d'une machine
    /// tournent toutes a la vitesse de la plus lente : c'est elle qui fixe le plafond.
    /// Rend 0 quand aucune frequence n'a ete lue : 0 veut dire « je ne sais pas », et aucun
    /// appelant n'a le droit d'en tirer une conclusion.
    /// </summary>
    public static double PerChannelPeakGBs(IReadOnlyList<MemoryModule> modules)
    {
        var mts = modules.Where(m => m.ConfiguredMhz > 0).Select(m => m.ConfiguredMhz).ToList();
        return mts.Count == 0 ? 0 : PeakGBs(mts.Min(), channels: 1);
    }

    /// <summary>Plafond theorique de <paramref name="channels"/> canaux a
    /// <paramref name="mts"/> MT/s. Le seul endroit ou vivent les 8 octets par transfert.</summary>
    public static double PeakGBs(int mts, int channels)
        => mts <= 0 || channels <= 0 ? 0 : mts * BytesPerTransfer * channels / 1000.0;

    /// <summary>
    /// Plafond au-dela duquel une mesure est forcement fausse : tous les emplacements peuples,
    /// chacun sur son canal. C'est le nombre a donner a <see cref="MemoryQuality"/>, parce qu'il
    /// majore ce que la machine peut rendre sans supposer sa topologie.
    /// </summary>
    public static double UpperBoundPeakGBs(IReadOnlyList<MemoryModule> modules)
        => PerChannelPeakGBs(modules) * modules.Count;

    /// <summary>
    /// Lignes de cache en vol, par la loi de Little : concurrence = debit x latence. Elle relie
    /// les deux nombres qu'on mesure deja et ne demande aucun banc supplementaire.
    ///
    /// Ce qu'elle explique, et qui compte pour ce produit : un debit modeste avec une bonne
    /// latence signifie que la machine ne garde pas assez d'acces en vol : le facteur limitant
    /// est le coeur, pas la memoire. C'est la raison pour laquelle
    /// <see cref="ProvenMinimumChannels"/> refuse de conclure vers le bas.
    /// </summary>
    public static double LinesInFlight(double bandwidthGBs, double latencyNs)
        => latencyNs <= 0 ? 0 : bandwidthGBs * latencyNs / 64.0;

    /// <summary>Le debit qui prouve le plus : le meilleur d'un fil et de plusieurs fils.</summary>
    public static double MeilleurDebit(MemoryMeasurement m) => Math.Max(m.BandwidthGBs, m.BandwidthMultiFilGBs);

    /// <summary>
    /// Un seul canal probable : au moins deux barrettes lues, et meme plusieurs fils a la fois ne depassent pas le plafond
    /// d'un canal. Plusieurs fils saturent le controleur sur un PC de bureau ; c'est ce qui autorise ici la conclusion
    /// vers le bas que le debit d'un seul fil n'autorise pas. Le plafond se calcule dans la meme unite que la mesure (Go/s
    /// decimaux : octets par seconde / 10^9, et MT/s x 8 octets / 1000). Sans debit multi-fil ou sans frequence : non.
    /// </summary>
    public static bool UnSeulCanalProbable(double debitMultiFilGBs, IReadOnlyList<MemoryModule> modules)
    {
        var parCanal = PerChannelPeakGBs(modules);
        return modules.Count >= 2 && parCanal > 0 && debitMultiFilGBs > 0 && debitMultiFilGBs <= parCanal;
    }

    /// <summary>
    /// Nombre de canaux que le debit mesure prouve. 1 signifie « rien de prouve au-dela d'un » :
    /// lire le commentaire de classe avant d'en faire un diagnostic de simple canal.
    /// </summary>
    public static int ProvenMinimumChannels(
        double measuredBandwidthGBs, IReadOnlyList<MemoryModule> modules)
    {
        var perChannel = PerChannelPeakGBs(modules);
        if (perChannel <= 0) return 1;

        var proven = (int)Math.Floor(measuredBandwidthGBs / (perChannel * ProofMargin)) + 1;
        return Math.Max(1, proven);
    }
}
