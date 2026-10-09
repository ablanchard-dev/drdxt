namespace DrDxt.Measurement.Network;

// -----------------------------------------------------------------------
// NS-9 — the network measurement INSTRUMENT.
//
// DrDXT measured frames and never measured the network. Without an instrument no
// network lever can be proven, and half the network folklore (TcpAckFrequency,
// Nagle, "gamer DNS") is myth — so NS-9 starts here, with measurement, and not
// with a tweak. Same doctrine as the rest: a gain is shown only when it is measured.
//
// What this measures, said plainly (the label matters as much as the number):
//   * ICMP echo round-trip to ONE named host, from this PC, right now.
//   * It is not the latency to the game server (routing and server load differ).
//   * It is not click-to-photon: nothing here includes input, render or display.
//   * ICMP loss is not game packet loss: routers routinely deprioritise or drop
//     ICMP under load while forwarding UDP game traffic untouched.
// A high reading is a lead to investigate, never a verdict — and never a "fix" to sell.
// -----------------------------------------------------------------------

/// <summary>One round-trip attempt. <see cref="RoundTripMs"/> is null when the probe did
/// not come back (timeout / unreachable / blocked) — a miss is a MISS, never a 0 ms sample
/// and never a max-latency sample.</summary>
public readonly record struct NetworkProbeSample(double? RoundTripMs);

/// <summary>Aggregate of one measurement window. Every latency field is nullable and is
/// null when nothing came back: with zero replies we report loss and say the latency is
/// UNKNOWN, we never print 0 ms.</summary>
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
    /// <summary>True when at least one reply came back, i.e. the latency fields carry a
    /// real measurement. False = loss-only report, latency UNKNOWN.</summary>
    public bool HasLatency => Received > 0;

    /// <summary>Localisation key for the caveat that must travel with these numbers
    /// (§22.3 honesty): ICMP to one host, not the game server, not click-to-photon.</summary>
    public string CaveatKey => "measure.network.icmp_caveat";
}

/// <summary>Pure aggregation — no I/O, fully unit-testable.</summary>
public static class NetworkLatencyStatistics
{
    /// <summary>Builds the report from the raw attempts, in order (order matters: jitter is
    /// defined on CONSECUTIVE samples).</summary>
    public static NetworkLatencyReport Summarize(string target, IReadOnlyList<NetworkProbeSample> samples)
    {
        int sent = samples.Count;
        // Only strictly-positive finite round-trips are real replies. A negative/NaN reading
        // from a misbehaving stack is treated as a miss, not as data.
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

    /// <summary>Nearest-rank p95 on the ascending list. With few samples this is simply the
    /// worst-but-one — honest for a short window, and it never interpolates a value that was
    /// never measured.</summary>
    private static double Percentile(IReadOnlyList<double> ascending, double fraction)
    {
        int rank = (int)Math.Ceiling(fraction * ascending.Count);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Count - 1)];
    }

    /// <summary>Jitter = mean absolute difference between CONSECUTIVE replies (the IPDV
    /// definition used by RTP/RFC 3550, and the one that matters in a shooter: a steady 40 ms
    /// beats a 20-60 ms sawtooth). Computed only across pairs where both probes replied — a
    /// lost probe breaks the chain instead of inventing a huge delta. Null with fewer than
    /// two consecutive replies.</summary>
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
