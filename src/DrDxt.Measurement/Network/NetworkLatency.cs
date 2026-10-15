namespace DrDxt.Measurement.Network;

// -----------------------------------------------------------------------
// L'instrument de mesure du réseau.
//
// Sans instrument, aucun levier réseau ne peut être prouvé, et la moitié du folklore réseau
// (TcpAckFrequency, Nagle, « DNS gamer ») est un mythe : le travail réseau commence donc ici, par
// la mesure, pas par un levier. Même doctrine que le reste : un gain ne s'affiche que mesuré.
//
// Ce que cela mesure, dit clairement (l'étiquette compte autant que le nombre) :
//   * l'aller-retour ICMP echo vers un seul hôte nommé, depuis ce PC, maintenant.
//   * Ce n'est pas la latence vers le serveur de jeu (routage et charge du serveur diffèrent).
//   * Ce n'est pas du clic-à-photon : rien ici n'inclut l'entrée, le rendu ou l'affichage.
//   * Une perte ICMP n'est pas une perte de paquets de jeu : les routeurs dépriorisent ou jettent
//     souvent l'ICMP sous charge tout en faisant passer intact le trafic UDP du jeu.
// Une valeur haute est une piste à creuser, jamais un verdict, et jamais une « correction » à vendre.
// -----------------------------------------------------------------------

/// <summary>Une tentative d'aller-retour. <see cref="RoundTripMs"/> est null quand la sonde n'est
/// pas revenue (délai dépassé / injoignable / bloquée) : un raté est un raté, jamais un relevé à
/// 0 ms ni un relevé à latence maximale.</summary>
public readonly record struct NetworkProbeSample(double? RoundTripMs);

/// <summary>Agrégat d'une fenêtre de mesure. Chaque champ de latence peut être null et l'est quand
/// rien n'est revenu : avec zéro réponse, on rapporte la perte et on dit la latence inconnue,
/// jamais 0 ms.</summary>
public sealed record NetworkLatencyReport(
    string Target,
    int Sent,
    int Received,
    double LossPercent,
    double? AvgMs,
    double? MinMs,
    double? MaxMs,
    double? P95Ms,
    double? JitterMs)
{
    /// <summary>Vrai quand au moins une réponse est revenue, c'est-à-dire que les champs de latence
    /// portent une vraie mesure. False = rapport de perte seule, latence inconnue.</summary>
    public bool HasLatency => Received > 0;

    /// <summary>Clé de localisation de la réserve qui doit accompagner ces nombres (honnêteté) :
    /// ICMP vers un seul hôte, pas le serveur de jeu, pas du clic-à-photon.</summary>
    public string CaveatKey => "measure.network.icmp_caveat";
}

/// <summary>Agrégation pure : aucune E/S, entièrement testable.</summary>
public static class NetworkLatencyStatistics
{
    /// <summary>Construit le rapport à partir des tentatives brutes, dans l'ordre (l'ordre compte :
    /// la gigue se définit sur des relevés consécutifs).</summary>
    public static NetworkLatencyReport Summarize(string target, IReadOnlyList<NetworkProbeSample> samples)
    {
        int sent = samples.Count;
        // Seuls les allers-retours finis strictement positifs sont de vraies réponses. Une valeur
        // négative / NaN d'une pile réseau défaillante compte comme un raté, pas comme une donnée.
        var rtts = samples
            .Select(s => s.RoundTripMs)
            .Where(ms => ms is > 0 && double.IsFinite(ms.Value))
            .Select(ms => ms!.Value)
            .ToList();
        int received = rtts.Count;
        double loss = sent == 0 ? 0 : 100.0 * (sent - received) / sent;

        if (received == 0)
            return new NetworkLatencyReport(target, sent, 0, sent == 0 ? 0 : 100, null, null, null, null, null);

        var sorted = rtts.OrderBy(x => x).ToList();
        return new NetworkLatencyReport(
            target, sent, received, loss,
            AvgMs: rtts.Average(),
            MinMs: sorted[0],
            MaxMs: sorted[^1],
            P95Ms: Percentile(sorted, 0.95),
            JitterMs: Jitter(samples));
    }

    /// <summary>p95 au rang le plus proche sur la liste croissante. Avec peu de relevés, c'est
    /// simplement l'avant-dernier pire : honnête pour une fenêtre courte, et n'interpole jamais une
    /// valeur jamais mesurée.</summary>
    private static double Percentile(IReadOnlyList<double> ascending, double fraction)
    {
        int rank = (int)Math.Ceiling(fraction * ascending.Count);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }

    /// <summary>Gigue = écart absolu moyen entre réponses consécutives (la définition IPDV de
    /// RTP/RFC 3550, celle qui compte dans un jeu de tir : 40 ms stables valent mieux qu'une dent de
    /// scie de 20 à 60 ms). Calculée seulement sur les paires où les deux sondes ont répondu : une
    /// sonde perdue coupe la chaîne au lieu d'inventer un énorme écart. Null avec moins de deux
    /// réponses consécutives.</summary>
    private static double? Jitter(IReadOnlyList<NetworkProbeSample> samples)
    {
        var deltas = new List<double>();
        for (int i = 1; i < samples.Count; i++)
        {
            double? a = samples[i - 1].RoundTripMs, b = samples[i].RoundTripMs;
            if (a is > 0 && b is > 0 && double.IsFinite(a.Value) && double.IsFinite(b.Value))
                deltas.Add(Math.Abs(b.Value - a.Value));
        }
        return deltas.Count == 0 ? null : deltas.Average();
    }
}
