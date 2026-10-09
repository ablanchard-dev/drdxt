using DrDxt.Core;

namespace DrDxt.Measurement.Network;

/// <summary>One round-trip attempt against a host. The seam exists so the meter is testable
/// with zero network access — and so a future probe (UDP to a real game server, TCP connect)
/// can replace ICMP without touching the aggregation.</summary>
public interface INetworkProbe
{
    /// <summary>Sends one probe. Returns a sample whose RoundTripMs is null when nothing came
    /// back. Never throws: a probe failure is a miss, not an exception.</summary>
    Task<NetworkProbeSample> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct);
}

public interface INetworkLatencyMeter
{
    /// <param name="warmupProbes">Sondes envoyees et JETEES avant la fenetre mesuree.
    /// Voir NetworkLatencyMeter pour pourquoi ce n'est pas une coquetterie.</param>
    Task<Result<NetworkLatencyReport>> MeasureAsync(
        string host, int probeCount, TimeSpan interval, TimeSpan timeout, CancellationToken ct,
        int warmupProbes = NetworkLatencyMeter.DefaultWarmupProbes);
}

/// <summary>
/// NS-9 instrument: sends N probes spaced by <c>interval</c> and aggregates them.
///
/// Spacing is deliberate — back-to-back probes measure a burst, not the connection a game
/// actually sees, and they can trip ICMP rate limiting (which would then be reported as
/// "packet loss" that does not exist). One probe per interval, in order, so jitter is
/// computed on consecutive samples (see NetworkLatencyStatistics).
///
/// heuristique : sequential and single-target on purpose. Multi-target (gateway vs ISP vs game
/// server) is what tells apart "my wifi" from "my ISP" — add it when a lever needs that
/// distinction, not before; the report shape already carries the target.
/// </summary>
public sealed class NetworkLatencyMeter : INetworkLatencyMeter
{
    /// <summary>
    /// Sondes de CHAUFFE, envoyees puis jetees. Mesure sur machine reelle (21/08/2026), meme
    /// cible, meme processus, trois fenetres consecutives : la premiere donnait avg 1,45 ms avec
    /// une pointe a 11 ms et jitter 0,95 ; les suivantes, avg 0,6 et jitter 0,21. Meme reseau,
    /// meme seconde. Le surcout venait du demarrage du processus (JIT, resolution DNS, premiere
    /// ouverture du socket ICMP), pas du lien.
    ///
    /// Sans chauffe, l'instrument attribue au reseau une lenteur qui lui appartient — et c'est
    /// exactement le genre de chiffre credible et faux que ce produit existe pour ne pas dire.
    /// (C'est arrive : deux conclusions « le premier saut est plus instable que tout
    /// l'internet » sur une mesure prise a froid.)
    /// </summary>
    public const int DefaultWarmupProbes = 3;

    private readonly INetworkProbe _probe;

    public NetworkLatencyMeter(INetworkProbe probe) => _probe = probe;

    public async Task<Result<NetworkLatencyReport>> MeasureAsync(
        string host, int probeCount, TimeSpan interval, TimeSpan timeout, CancellationToken ct,
        int warmupProbes = DefaultWarmupProbes)
    {
        if (string.IsNullOrWhiteSpace(host))
            return Result<NetworkLatencyReport>.Fail(ErrorCodes.MeasureInvalidInput, "No host to probe.");
        if (probeCount <= 0)
            return Result<NetworkLatencyReport>.Fail(ErrorCodes.MeasureInvalidInput, "Probe count must be >= 1.");
        if (timeout <= TimeSpan.Zero)
            return Result<NetworkLatencyReport>.Fail(ErrorCodes.MeasureInvalidInput, "Probe timeout must be > 0.");

        // Chauffe : on envoie, on ne garde rien. Un echec ici ne fait pas echouer la mesure —
        // c'est une preparation, pas une observation.
        for (int w = 0; w < Math.Max(0, warmupProbes) && !ct.IsCancellationRequested; w++)
        {
            try { await _probe.ProbeAsync(host, timeout, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* jete */ }
        }

        var samples = new List<NetworkProbeSample>(probeCount);
        for (int i = 0; i < probeCount; i++)
        {
            if (ct.IsCancellationRequested) break;

            NetworkProbeSample sample;
            try
            {
                sample = await _probe.ProbeAsync(host, timeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // cancelled mid-window: keep what we have, report on it honestly.
            }
            catch (Exception)
            {
                // A throwing probe is a MISS, never a crash and never a fabricated reading.
                sample = new NetworkProbeSample(null);
            }
            samples.Add(sample);

            if (i < probeCount - 1 && interval > TimeSpan.Zero)
            {
                try { await Task.Delay(interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        // A window where nothing was sent is not a 100%-loss result — it is no measurement at
        // all. Say so rather than publishing a loss figure we did not observe.
        if (samples.Count == 0)
            return Result<NetworkLatencyReport>.Fail(ErrorCodes.MeasureNoSamples,
                "No network probe was sent (cancelled before the first probe): nothing measured.");

        return Result<NetworkLatencyReport>.Ok(NetworkLatencyStatistics.Summarize(host, samples));
    }
}
