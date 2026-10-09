using System.Net.NetworkInformation;
using DrDxt.Core;

namespace DrDxt.Detection.Network;

/// <summary>07/10/2026 (D6) : les cartes actives qui ont une passerelle IPv4 ou IPv6, sans les tunnels (VPN) ni la boucle
/// locale, et si elles sont sans fil (NetworkInterfaceType.Wireless80211). Lecture seule, sans droits ; <c>null</c> = non lu.</summary>
public static class CartesVersInternet
{
    /// <summary>Revue F-43 : les noms d'adaptateurs que Windows type « Ethernet » sans câble derrière (VPN, machines
    /// virtuelles, partage de connexion USB d'un téléphone). Liste de motifs relevés, pas une source : un adaptateur absent
    /// d'ici compte comme un câble.</summary>
    private static readonly string[] MotifsVirtuels =
        ["virtual", "virtuel", "tap-", "tap ", "vpn", "wireguard", "hamachi", "hyper-v", "vmware", "virtualbox", "remote ndis", "tailscale", "zerotier"];

    internal static bool Virtuelle(string description) => MotifsVirtuels.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destination, out uint index);

    /// <summary>L'index de la carte par laquelle Windows envoie vers une adresse d'internet (1.1.1.1), ou null.</summary>
    private static int? IndexDeLaRoute()
    {
        try { return GetBestInterface(0x01010101, out var index) == 0 ? (int)index : null; }
        catch (Exception) { return null; }
    }

    private static int? Index(NetworkInterface n)
    {
        try { return n.GetIPProperties().GetIPv4Properties()?.Index; }
        catch (Exception) { return null; }
    }

    public static IReadOnlyList<CarteVersInternet>? Lire()
    {
        try
        {
            var route = IndexDeLaRoute();
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
                            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)
                                                                             && !g.Address.Equals(System.Net.IPAddress.IPv6Any)))
                .Select(n => new CarteVersInternet(n.Name, n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    Virtuelle(n.Description) || Virtuelle(n.Name), route is { } r && Index(n) == r))
                .ToList();
        }
        catch (Exception) { return null; }
    }
}
