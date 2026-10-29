using DrDxt.Core;

namespace DrDxt.Measurement.Network;

/// <summary>Une tentative d'aller-retour vers un hôte : testable sans réseau, et remplaçable (UDP vers un serveur de jeu,
/// TCP) sans toucher à l'agrégation.</summary>
public interface INetworkProbe
{
    /// <summary>Envoie une sonde. Rend un relevé dont RoundTripMs est null quand rien n'est revenu.
    /// Ne lève jamais : un échec de sonde est un raté, pas une exception.</summary>
    Task<NetworkProbeSample> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct);
}

public interface INetworkLatencyMeter
{
    /// <param name="warmupProbes">Sondes envoyees et jetees avant la fenetre mesuree (voir
    /// <see cref="NetworkLatencyMeter.DefaultWarmupProbes"/>).</param>
    Task<Result<NetworkLatencyReport>> MeasureAsync(
        string host, int probeCount, TimeSpan interval, TimeSpan timeout, CancellationToken ct,
        int warmupProbes = NetworkLatencyMeter.DefaultWarmupProbes);
}

/// <summary>
/// Instrument réseau : N sondes espacées de <c>interval</c>, agrégées. Collées, elles mesureraient une rafale et
/// pourraient déclencher la limitation de débit ICMP (une fausse « perte de paquets ») ; dans l'ordre, la gigue se
/// calcule sur des relevés consécutifs (NetworkLatencyStatistics).
/// heuristique : séquentiel et à cible unique exprès. Plusieurs cibles (passerelle / FAI / serveur de jeu) sépareraient
/// « mon Wi-Fi » de « mon FAI » : à ajouter quand un levier en a besoin ; le rapport porte déjà la cible.
/// </summary>
public sealed class NetworkLatencyMeter : INetworkLatencyMeter
{
    /// <summary>
    /// Sondes de chauffe, envoyees puis jetees. Mesure sur machine reelle, meme cible et processus, trois fenetres
    /// consecutives : la premiere avg 1,45 ms, pointe a 11 ms, jitter 0,95 ; les suivantes avg 0,6, jitter 0,21. Le
    /// surcout venait du demarrage du processus (JIT, DNS, premiere ouverture du socket ICMP) : sans chauffe, il serait
    /// attribue au reseau.
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

        // Chauffe : rien n'est garde, et un echec ne fait pas echouer la mesure.
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
                break; // annulé en pleine fenêtre : garder ce qu'on a, en rendre compte honnêtement.
            }
            catch (Exception)
            {
                // Une sonde qui lève est un raté, jamais un plantage ni un relevé fabriqué.
                sample = new NetworkProbeSample(null);
            }
            samples.Add(sample);

            if (i < probeCount - 1 && interval > TimeSpan.Zero)
            {
                try { await Task.Delay(interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        // Rien d'envoyé n'est pas 100 % de perte : pas une mesure du tout, et c'est dit.
        if (samples.Count == 0)
            return Result<NetworkLatencyReport>.Fail(ErrorCodes.MeasureNoSamples,
                "No network probe was sent (cancelled before the first probe): nothing measured.");

        return Result<NetworkLatencyReport>.Ok(NetworkLatencyStatistics.Summarize(host, samples));
    }
}
