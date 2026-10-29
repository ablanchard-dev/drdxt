using DrDxt.Core;

namespace DrDxt.Measurement.Memory;

/// <summary>
/// Ce que la mesure apprend de la memoire et que la lecture des chaines `DeviceLocator` (`MemoryTopologyAdvisor`) ne
/// dit pas : un canal a un debit plafond, un debit mesure au-dessus prouve plusieurs canaux. Jamais a l'envers : un seul
/// fil ne sature pas forcement deux canaux (le coeur limite ses acces en vol), donc `1` veut dire « au moins un »,
/// Unknown, jamais « simple canal ».
/// </summary>
public static class MemoryInference
{
    /// <summary>Octets par transfert d'un canal DDR : le bus fait 64 bits. Valeur de la norme.</summary>
    private const double BytesPerTransfer = 8.0;

    /// <summary>
    /// Marge au-dessus du plafond avant de conclure. Choix produit : frequence arrondie, horloge qui derive ; un debit a
    /// 99,5 % du plafond est plutot une mesure genereuse qu'un canal de plus.
    /// </summary>
    private const double ProofMargin = 1.02;

    /// <summary>
    /// Debit plafond d'un canal, en Go/s, a la frequence de la barrette la plus lente (toutes tournent a sa vitesse).
    /// 0 = aucune frequence lue, « je ne sais pas » : aucune conclusion.
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
    /// Plafond au-dela duquel une mesure est fausse (chaque barrette sur son canal) : le majorant a donner a
    /// <see cref="MemoryQuality"/>, sans supposer la topologie.
    /// </summary>
    public static double UpperBoundPeakGBs(IReadOnlyList<MemoryModule> modules)
        => PerChannelPeakGBs(modules) * modules.Count;

    /// <summary>
    /// Lignes de cache en vol, par la loi de Little : concurrence = debit x latence, sans banc de plus. Un debit modeste
    /// avec une bonne latence : le coeur limite, pas la memoire, d'ou le refus de <see cref="ProvenMinimumChannels"/> de
    /// conclure vers le bas.
    /// </summary>
    public static double LinesInFlight(double bandwidthGBs, double latencyNs)
        => latencyNs <= 0 ? 0 : bandwidthGBs * latencyNs / 64.0;

    /// <summary>Le debit qui prouve le plus : le meilleur d'un fil et de plusieurs fils.</summary>
    public static double MeilleurDebit(MemoryMeasurement m) => Math.Max(m.BandwidthGBs, m.BandwidthMultiFilGBs);

    /// <summary>
    /// Un seul canal probable : au moins deux barrettes, et meme plusieurs fils (qui saturent le controleur d'un PC de
    /// bureau, ce qu'un seul fil ne fait pas) restent sous le plafond d'un canal. Meme unite que la mesure (Go/s decimaux :
    /// octets/s / 10^9, MT/s x 8 / 1000). Sans debit multi-fil ou sans frequence : non.
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
