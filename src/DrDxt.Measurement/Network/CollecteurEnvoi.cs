using System.Net.NetworkInformation;

namespace DrDxt.Measurement.Network;

/// <summary>
/// Releve, a pas regulier, ce que la machine envoie (compteur des cartes actives,
/// lecture locale) et le ping vers la reference internet. N'envoie rien d'autre que ce ping :
/// c'est le trafic du joueur qui fait la charge, jamais DrDXT.
/// </summary>
public sealed class CollecteurEnvoi
{
    private readonly INetworkProbe _ping;
    private readonly string _cible;
    private readonly Func<long> _octetsEnvoyes;

    public CollecteurEnvoi(INetworkProbe ping, string cible, Func<long>? octetsEnvoyes = null)
        => (_ping, _cible, _octetsEnvoyes) = (ping, cible, octetsEnvoyes ?? OctetsEnvoyesCartesActives);

    public async Task<IReadOnlyList<EchantillonEnvoi>> CollecterAsync(TimeSpan duree, TimeSpan pas, CancellationToken ct)
    {
        var sortie = new List<EchantillonEnvoi>();
        var fin = DateTime.UtcNow + duree;
        var avant = _octetsEnvoyes();
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        while (DateTime.UtcNow < fin && !ct.IsCancellationRequested)
        {
            var ping = await _ping.ProbeAsync(_cible, pas, ct).ConfigureAwait(false);
            var reste = pas - t0.Elapsed;
            if (reste > TimeSpan.Zero) await Task.Delay(reste, ct).ConfigureAwait(false);

            var apres = _octetsEnvoyes();
            var secondes = t0.Elapsed.TotalSeconds;
            t0.Restart();
            // Un compteur qui recule (carte reinitialisee) ne fabrique pas un envoi negatif.
            var mbps = apres >= avant && secondes > 0 ? (apres - avant) * 8 / secondes / 1_000_000 : 0;
            avant = apres;
            sortie.Add(new EchantillonEnvoi(mbps, ping.RoundTripMs));
        }
        return sortie;
    }

    private static long OctetsEnvoyesCartesActives()
    {
        long total = 0;
        foreach (var carte in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (carte.OperationalStatus != OperationalStatus.Up) continue;
            if (carte.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                // Seule la carte qui porte la route par defaut envoie vers internet : les adaptateurs
                // virtuels (VirtualBox, Hyper-V) ne comptent pas.
                if (!carte.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any))) continue;
                total += carte.GetIPStatistics().BytesSent;
            }
            catch { /* carte disparue entre deux lectures */ }
        }
        return total;
    }
}
