using System.Net.NetworkInformation;

namespace DrDxt.Measurement.Network;

/// <summary>
/// ICMP echo par la pile de l'OS (<see cref="Ping"/>, BCL .NET : aucune dépendance, aucun pilote, aucun socket brut).
/// <c>Ping</c> rend des millisecondes entières : 0,3 ms en local se lit 0, et la gigue est quantifiée à 1 ms ; un
/// Stopwatch autour mesurerait l'ordonnancement .NET autant que le réseau. Une réponse à 0 ms est reçue, rapportée à
/// 0,5 ms (le milieu de [0,1)), jamais « pas de réponse ».
/// Tout ce qui n'est pas Success (délai, injoignable, TTL expiré, ICMP filtré) est une perte, et le rapport dit qu'une
/// perte ICMP n'est pas une perte de paquets de jeu.
/// </summary>
public sealed class SystemPingProbe : INetworkProbe
{
    // Milieu de [0,1) ms : à 0, une réponse sous la milliseconde se confondrait en aval avec « pas de donnée » (0 y est
    // filtré comme invalide).
    private const double SubMillisecondReplyMs = 0.5;

    public async Task<NetworkProbeSample> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, timeout, cancellationToken: ct).ConfigureAwait(false);
            if (reply.Status != IPStatus.Success)
                return new NetworkProbeSample(null);

            double ms = reply.RoundtripTime;
            return new NetworkProbeSample(ms <= 0 ? SubMillisecondReplyMs : ms);
        }
        catch (OperationCanceledException)
        {
            throw; // l'annulation regarde l'appelant, ce n'est pas un verdict réseau.
        }
        catch (Exception)
        {
            // PingException (échec DNS, pas de route), erreurs de socket, courses de libération : un raté.
            return new NetworkProbeSample(null);
        }
    }
}
