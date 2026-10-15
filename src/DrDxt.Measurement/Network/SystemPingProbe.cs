using System.Net.NetworkInformation;

namespace DrDxt.Measurement.Network;

/// <summary>
/// ICMP echo par la pile de l'OS (<see cref="Ping"/>, BCL .NET : aucune dépendance, aucun pilote,
/// aucun socket brut, rien d'installé).
///
/// Précision, dite honnêtement : <c>Ping</c> rend des millisecondes entières. Sur un réseau local,
/// un vrai aller-retour de 0,3 ms se lit 0 ms, et la gigue tirée de ces relevés est quantifiée à
/// 1 ms. On n'entoure pas l'appel d'un Stopwatch pour simuler des décimales sous la milliseconde :
/// cela mesurerait l'ordonnancement .NET autant que le réseau, et une fausse précision est
/// exactement ce que ce produit refuse. Une réponse à 0 ms est une vraie réponse (elle compte comme
/// reçue, et comme un relevé de valeur ~0) : elle est rapportée comme la plus petite non-perte
/// mesurable, 0,5 ms, le milieu de l'intervalle [0,1), et jamais comme « pas de réponse ».
///
/// Tout ce qui n'est pas une réponse Success (délai dépassé, injoignable, TTL expiré, ICMP filtré
/// par un pare-feu) est un raté. Il est agrégé comme perte, et la réserve portée par le rapport dit
/// clairement qu'une perte ICMP n'est pas une perte de paquets de jeu.
/// </summary>
public sealed class SystemPingProbe : INetworkProbe
{
    // Milieu de l'intervalle [0,1) ms : une réponse sous la milliseconde est réelle, et la rapporter
    // à 0 la rendrait impossible à distinguer de « pas de donnée » en aval (chaque champ de latence
    // peut être null et 0 est filtré comme invalide).
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
