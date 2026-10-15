using System.Buffers.Binary;
using System.Net;

namespace DrDxt.Detection.Network;

/// <summary>Un envoi UDP vu par Windows : qui envoie, combien, et vers où.</summary>
public sealed record EnvoiUdp(int Pid, int Octets, IPAddress Destination, int PortDestination);

/// <summary>
/// Les jeux parlent en UDP, et la table UDP de Windows ne donne pas l'adresse en face.
/// Le fournisseur Microsoft-Windows-Kernel-Network la publie à chaque envoi (événement 42, « Data sent over UDP
/// protocol », mot-clé IPv4 0x10). Forme relevée sur la machine le 03/10 par logman + tracerpt : PID, size, daddr,
/// saddr, dport, sport, seqnum, connid. Les adresses et les ports sont dans l'ordre du réseau (octet fort d'abord),
/// PID et taille dans l'ordre de la machine.
/// </summary>
public static class EnvoisUdp
{
    public static readonly Guid Fournisseur = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    public const ushort EvenementEnvoiIpv4 = 42;
    public const ulong MotCleIpv4 = 0x10;
    private const int TailleMinimale = 20;

    /// <summary>Lit la charge d'un événement 42, ou <c>null</c> si elle est trop courte pour être cet événement.</summary>
    public static EnvoiUdp? Decoder(ReadOnlySpan<byte> charge)
    {
        if (charge.Length < TailleMinimale) return null;
        return new EnvoiUdp(
            BinaryPrimitives.ReadInt32LittleEndian(charge),
            BinaryPrimitives.ReadInt32LittleEndian(charge[4..]),
            new IPAddress(charge.Slice(8, 4)),
            BinaryPrimitives.ReadUInt16BigEndian(charge[16..]));
    }

    /// <summary>Le serveur de la partie : la destination publique qui reçoit le plus d'octets du jeu. Les adresses
    /// locales (box, réseau de la maison) et le DNS ne sont jamais un serveur de jeu.</summary>
    public static IPEndPoint? ServeurDeLaPartie(IEnumerable<EnvoiUdp> envois, int pidDuJeu) => envois
        .Where(e => e.Pid == pidDuJeu && e.PortDestination != 53 && EstPublique(e.Destination))
        .GroupBy(e => (e.Destination, e.PortDestination))
        .OrderByDescending(g => g.Sum(e => (long)e.Octets))
        .Select(g => new IPEndPoint(g.Key.Destination, g.Key.PortDestination))
        .FirstOrDefault();

    internal static bool EstPublique(IPAddress a)
    {
        var o = a.GetAddressBytes();
        if (o.Length != 4) return false;
        return !(o[0] == 10 || o[0] == 127 || o[0] == 0 || o[0] >= 224
                 || (o[0] == 172 && o[1] is >= 16 and <= 31)
                 || (o[0] == 192 && o[1] == 168)
                 || (o[0] == 169 && o[1] == 254)
                 || (o[0] == 100 && o[1] is >= 64 and <= 127));
    }
}
