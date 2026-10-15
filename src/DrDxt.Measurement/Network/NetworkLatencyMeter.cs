using DrDxt.Core;

namespace DrDxt.Measurement.Network;

/// <summary>Une tentative d'aller-retour vers un hôte. La couture existe pour tester le mesureur
/// sans aucun accès réseau, et pour qu'une future sonde (UDP vers un vrai serveur de jeu,
/// connexion TCP) remplace l'ICMP sans toucher à l'agrégation.</summary>
public interface INetworkProbe
{
    /// <summary>Envoie une sonde. Rend un relevé dont RoundTripMs est null quand rien n'est revenu.
    /// Ne lève jamais : un échec de sonde est un raté, pas une exception.</summary>
    Task<NetworkProbeSample> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct);
}

public interface INetworkLatencyMeter
{
    /// <param name="warmupProbes">Sondes envoyees et jetees avant la fenetre mesuree.
    /// Voir NetworkLatencyMeter pour pourquoi ce n'est pas une coquetterie.</param>
    Task<Result<NetworkLatencyReport>> MeasureAsync(
        string host, int probeCount, TimeSpan interval, TimeSpan timeout, CancellationToken ct,
        int warmupProbes = NetworkLatencyMeter.DefaultWarmupProbes);
}

/// <summary>
/// Instrument réseau : envoie N sondes espacées de <c>interval</c> et les agrège.
///
/// L'espacement est voulu : des sondes collées mesurent une rafale, pas la connexion que voit
/// vraiment un jeu, et elles peuvent déclencher la limitation de débit ICMP (rapportée ensuite
/// comme une « perte de paquets » qui n'existe pas). Une sonde par intervalle, dans l'ordre, pour
/// que la gigue se calcule sur des relevés consécutifs (voir NetworkLatencyStatistics).
///
/// heuristique : séquentiel et à cible unique exprès. Plusieurs cibles (passerelle / FAI /
/// serveur de jeu) distinguent « mon Wi-Fi » de « mon FAI » : à ajouter quand un levier a besoin
/// de cette distinction, pas avant ; la forme du rapport porte déjà la cible.
/// </summary>
public sealed class NetworkLatencyMeter : INetworkLatencyMeter
{
    /// <summary>
    /// Sondes de chauffe, envoyees puis jetees. Mesure sur machine reelle, meme
    /// cible, meme processus, trois fenetres consecutives : la premiere donnait avg 1,45 ms avec
    /// une pointe a 11 ms et jitter 0,95 ; les suivantes, avg 0,6 et jitter 0,21. Meme reseau,
    /// meme seconde. Le surcout venait du demarrage du processus (JIT, resolution DNS, premiere
    /// ouverture du socket ICMP), pas du lien.
    ///
    /// Sans chauffe, l'instrument attribue au reseau une lenteur qui lui appartient, et c'est
    /// exactement le genre de chiffre credible et faux que ce produit existe pour ne pas dire
    /// (par exemple « le premier saut est plus instable que tout l'internet » sur une mesure prise
    /// a froid).
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

        // Chauffe : on envoie, on ne garde rien. Un echec ici ne fait pas echouer la mesure :
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

        // Une fenêtre où rien n'a été envoyé n'est pas une perte de 100 % : ce n'est pas une mesure du
        // tout. Le dire plutôt que publier un chiffre de perte non observé.
        if (samples.Count == 0)
            return Result<NetworkLatencyReport>.Fail(ErrorCodes.MeasureNoSamples,
                "No network probe was sent (cancelled before the first probe): nothing measured.");

        return Result<NetworkLatencyReport>.Ok(NetworkLatencyStatistics.Summarize(host, samples));
    }
}
