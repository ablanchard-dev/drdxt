using System.Net.NetworkInformation;

namespace DrDxt.Measurement.Network;

/// <summary>
/// ICMP echo through the OS stack (<see cref="Ping"/>, .NET BCL — no dependency, no driver,
/// no raw socket, nothing installed).
///
/// Precision, stated honestly: <c>Ping</c> reports whole milliseconds. On a LAN a real 0.3 ms
/// round-trip reads as 0 ms, and jitter derived from these samples is quantised to 1 ms. We do
/// Not wrap a Stopwatch around the call to fake sub-millisecond digits: that would measure .NET
/// scheduling as much as the network — false precision is exactly what this product refuses.
/// A 0 ms reply is a real reply (it counts as received, and as a sample of value ~0), so it is
/// reported as the smallest measurable non-loss: 0.5 ms, the midpoint of the [0,1) bucket, and
/// never as "no reply".
///
/// Anything that is not a Success reply — timeout, unreachable, TTL expired, ICMP filtered by a
/// firewall — is a MISS. It is aggregated as loss, and the caveat carried by the report says
/// plainly that ICMP loss is not game packet loss.
/// </summary>
public sealed class SystemPingProbe : INetworkProbe
{
    // Midpoint of the [0,1) ms bucket: a sub-millisecond reply is real, and reporting it as 0
    // would make it indistinguishable from "no data" downstream (every latency field is
    // nullable and 0 is filtered out as invalid).
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
            throw; // cancellation is the caller's business, not a network verdict.
        }
        catch (Exception)
        {
            // PingException (DNS failure, no route), socket errors, disposal races: a MISS.
            return new NetworkProbeSample(null);
        }
    }
}
