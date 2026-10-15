using System.Net.NetworkInformation;
using DrDxt.Core;

namespace DrDxt.Measurement.Network;

/// <summary>
/// Compteurs d'une carte reseau a un instant donne. Ce sont des totaux depuis le demarrage de
/// la machine : pris seuls ils ne disent rien de l'instant present (mesure sur une machine
/// saine : 347 paquets ignores sur 57 789 905 depuis le boot, inutilisable pour diagnostiquer
/// une partie en cours). Ils ne servent que par difference entre deux instants, voir
/// <see cref="NicErrorCounters.Diff"/>.
/// </summary>
public sealed record NicCounterSnapshot(
    long ReceivedDiscarded,
    long ReceivedErrors,
    long SentDiscarded,
    long SentErrors,
    long ReceivedPackets,
    long SentPackets);

/// <summary>
/// Ce qui s'est passe sur la carte pendant une fenetre de mesure.
///
/// Complementaire du ping, pas redondant : <see cref="NetworkLatencyMeter"/> mesure la route
/// (latence, perte, gigue), ces compteurs mesurent la carte : cable abime, port en
/// derangement, pilote qui jette des trames. Un lien peut pinguer parfaitement tout en
/// perdant des paquets a l'interface.
/// </summary>
public sealed record NicErrorDelta(
    long ReceivedDiscarded,
    long ReceivedErrors,
    long SentDiscarded,
    long SentErrors,
    long ReceivedPackets,
    long SentPackets,
    bool CounterReset)
{
    /// <summary>Vrai quand rien d'anormal ne s'est produit dans la fenetre. Un compteur qui a
    /// Recule n'est pas « propre » : la carte a ete reinitialisee et on ne sait plus ce qui
    /// s'est passe. Repondre « tout va bien » a partir d'une perte d'information serait une
    /// bonne nouvelle fabriquee.</summary>
    public bool IsClean =>
        !CounterReset &&
        ReceivedDiscarded == 0 && ReceivedErrors == 0 &&
        SentDiscarded == 0 && SentErrors == 0;

    /// <summary>Part des paquets recus qui ont ete ignores, dans la fenetre (jamais depuis le
    /// boot). <c>null</c> quand aucun paquet n'a circule ou que les compteurs ont ete
    /// reinitialises : un taux calcule sur rien serait un « 0 % » invente.</summary>
    public double? ReceivedDiscardPercent =>
        CounterReset || ReceivedPackets <= 0
            ? null
            : 100.0 * ReceivedDiscarded / ReceivedPackets;
}

/// <summary>Source des compteurs. L'interface existe pour que la logique de difference soit
/// testable sans carte reseau reelle.</summary>
public interface INicCounterSource
{
    Result<NicCounterSnapshot> Read(string interfaceId);
}

public static class NicErrorCounters
{
    /// <summary>Difference entre deux releves. Si un compteur a recule, la carte a ete
    /// reinitialisee pendant la fenetre : on le declare (<see cref="NicErrorDelta.CounterReset"/>),
    /// on ne rend jamais un nombre negatif, et on ne calcule aucun taux. Unknown vaut mieux
    /// qu'un chiffre faux.</summary>
    public static NicErrorDelta Diff(NicCounterSnapshot before, NicCounterSnapshot after)
    {
        var reset =
            after.ReceivedDiscarded < before.ReceivedDiscarded ||
            after.ReceivedErrors < before.ReceivedErrors ||
            after.SentDiscarded < before.SentDiscarded ||
            after.SentErrors < before.SentErrors ||
            after.ReceivedPackets < before.ReceivedPackets ||
            after.SentPackets < before.SentPackets;

        static long Sub(long a, long b) => a - b < 0 ? 0 : a - b;

        return new NicErrorDelta(
            ReceivedDiscarded: Sub(after.ReceivedDiscarded, before.ReceivedDiscarded),
            ReceivedErrors: Sub(after.ReceivedErrors, before.ReceivedErrors),
            SentDiscarded: Sub(after.SentDiscarded, before.SentDiscarded),
            SentErrors: Sub(after.SentErrors, before.SentErrors),
            ReceivedPackets: Sub(after.ReceivedPackets, before.ReceivedPackets),
            SentPackets: Sub(after.SentPackets, before.SentPackets),
            CounterReset: reset);
    }
}

/// <summary>Lecture reelle via le BCL (<see cref="NetworkInterface"/>) : aucun WMI, aucun
/// pilote, aucune elevation.</summary>
public sealed class SystemNicCounterSource : INicCounterSource
{
    public Result<NicCounterSnapshot> Read(string interfaceId)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, interfaceId, StringComparison.OrdinalIgnoreCase));

            if (nic is null)
                return Result<NicCounterSnapshot>.Fail(
                    ErrorCodes.MeasureNicUnreadable, $"Network interface '{interfaceId}' was not found.");

            var s = nic.GetIPv4Statistics();
            return Result<NicCounterSnapshot>.Ok(new NicCounterSnapshot(
                ReceivedDiscarded: s.IncomingPacketsDiscarded,
                ReceivedErrors: s.IncomingPacketsWithErrors,
                SentDiscarded: s.OutgoingPacketsDiscarded,
                SentErrors: s.OutgoingPacketsWithErrors,
                ReceivedPackets: s.UnicastPacketsReceived,
                SentPackets: s.UnicastPacketsSent));
        }
        catch (NetworkInformationException ex)
        {
            // Une carte qui disparait pendant la lecture est un echec propre, jamais un zero.
            return Result<NicCounterSnapshot>.Fail(ErrorCodes.MeasureNicUnreadable, ex.Message);
        }
    }
}
