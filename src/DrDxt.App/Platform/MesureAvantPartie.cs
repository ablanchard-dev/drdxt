using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DrDxt.Application.Parties;

namespace DrDxt.App.Platform;

/// <summary>
/// Les quelques secondes de mesure du bouton « prêt à jouer ». Rien n'est envoyé hors de la
/// machine : dix pings vers la box et dix vers 1.1.1.1 (comme le reste du produit), les programmes voisins sur 600 ms,
/// la mémoire libre, et la température si HWiNFO la publie. Toutes les lectures tournent en parallèle.
/// </summary>
public static class MesureAvantPartie
{
    public const int Pings = 10;
    private static readonly IPAddress Internet = IPAddress.Parse("1.1.1.1");

    public static async Task<EtatAvantPartie> MesurerAsync(ILecteurDeVoisins voisins, CancellationToken ct)
    {
        var lusVoisins = Task.Run(() => voisins.Lire(null, ct), ct);
        var box = Passerelle();
        var lusBox = box is null ? Task.FromResult<(double?, double?)>((null, null)) : PingerAsync(box);
        var lusInternet = PingerAsync(Internet);
        await Task.WhenAll(lusVoisins, lusBox, lusInternet).ConfigureAwait(false);
        var (pingBox, pertesBox) = lusBox.Result;
        var (pingNet, pertesNet) = lusInternet.Result;
        return new EtatAvantPartie(
            lusVoisins.Result?.Select(v => (v.Nom, v.ProcesseurPourcent)).ToList(),
            pingBox, pertesBox, pingNet, pertesNet,
            DrDxt.Detection.Cpu.TemperatureProcesseur.Lire()?.Celsius,
            DrDxt.Measurement.CommitPressureReader.LibrePhysique());
    }

    /// <summary>La même mesure pendant la partie, pour le relevé : box, internet, pertes vers internet.</summary>
    public static async Task<(double? Box, double? Internet, double? Pertes)> ReseauAsync(CancellationToken ct)
    {
        var box = Passerelle();
        var lusBox = box is null ? Task.FromResult<(double?, double?)>((null, null)) : PingerAsync(box);
        var lusInternet = PingerAsync(Internet);
        await Task.WhenAll(lusBox, lusInternet).ConfigureAwait(false);
        return (lusBox.Result.Item1, lusInternet.Result.Mediane, lusInternet.Result.Pertes);
    }

    /// <summary>La médiane des allers-retours et le pourcentage de pertes ; tout perdu : médiane inconnue, 100 %.</summary>
    private static async Task<(double? Mediane, double? Pertes)> PingerAsync(IPAddress cible)
    {
        try
        {
            using var ping = new Ping();
            var rtt = new List<long>();
            var perdus = 0;
            for (var i = 0; i < Pings; i++)
            {
                var r = await ping.SendPingAsync(cible, 1000).ConfigureAwait(false);
                if (r.Status == IPStatus.Success) rtt.Add(r.RoundtripTime); else perdus++;
                await Task.Delay(100).ConfigureAwait(false);
            }
            rtt.Sort();
            double? mediane = rtt.Count == 0 ? null : rtt.Count % 2 == 1 ? rtt[rtt.Count / 2] : (rtt[rtt.Count / 2 - 1] + rtt[rtt.Count / 2]) / 2.0;
            return (mediane, 100.0 * perdus / Pings);
        }
        catch (Exception) { return (null, null); }
    }

    /// <summary>La passerelle de la carte par laquelle passe vraiment la route vers internet.
    /// La première carte active avec une passerelle pouvait être un VPN ou un Wi-Fi secondaire. Une prise UDP « connectée »
    /// à 1.1.1.1 n'envoie rien, mais Windows y choisit l'adresse locale de la route : sa carte porte la bonne passerelle.</summary>
    internal static IPAddress? Passerelle()
    {
        try
        {
            using var prise = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            prise.Connect(Internet, 53);
            if (prise.LocalEndPoint is not IPEndPoint { Address: var locale }) return null;
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => n.GetIPProperties())
                .Where(p => p.UnicastAddresses.Any(u => u.Address.Equals(locale)))
                .SelectMany(p => p.GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (Exception) { return null; }
    }
}
