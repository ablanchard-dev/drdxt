using System.Net;
using DrDxt.Detection.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Network;

// 03/10/2026 (boucle 2D2P, P3.1b) : trouver le serveur d'une partie en UDP. La charge de test reprend l'envoi relevé sur
// la machine le 03/10 : PID 3208, 30 octets, 192.168.1.112:55088 vers 192.168.1.254:53.
public sealed class EnvoisUdpTests
{
    private static byte[] Charge(int pid, int octets, byte[] daddr, ushort dport) =>
    [
        .. BitConverter.GetBytes(pid), .. BitConverter.GetBytes(octets), .. daddr, 192, 168, 1, 112,
        (byte)(dport >> 8), (byte)dport, 0xD7, 0x30, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    [Fact]
    public void L_evenement_releve_sur_la_machine_se_relit_a_l_identique()
    {
        var e = EnvoisUdp.Decoder(Charge(3208, 30, [192, 168, 1, 254], 53));
        e.Should().Be(new EnvoiUdp(3208, 30, IPAddress.Parse("192.168.1.254"), 53));
    }

    [Fact]
    public void Une_charge_trop_courte_n_est_pas_lue() => EnvoisUdp.Decoder(new byte[19]).Should().BeNull();

    [Fact]
    public void Le_serveur_est_la_destination_publique_qui_recoit_le_plus_du_jeu()
    {
        var jeu = 4242;
        var envois = new[]
        {
            new EnvoiUdp(jeu, 900, IPAddress.Parse("185.34.107.20"), 3074),
            new EnvoiUdp(jeu, 900, IPAddress.Parse("185.34.107.20"), 3074),
            new EnvoiUdp(jeu, 1500, IPAddress.Parse("34.120.1.9"), 443),
            new EnvoiUdp(jeu, 9000, IPAddress.Parse("192.168.1.254"), 3074),
            new EnvoiUdp(jeu, 9000, IPAddress.Parse("8.8.8.8"), 53),
            new EnvoiUdp(99, 90000, IPAddress.Parse("151.101.1.1"), 443),
        };
        EnvoisUdp.ServeurDeLaPartie(envois, jeu).Should().Be(new IPEndPoint(IPAddress.Parse("185.34.107.20"), 3074),
            "1 800 octets au total contre 1 500 ; la box, le DNS et les autres programmes ne comptent pas");
    }

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("185.34.107.20", true)]
    public void Les_adresses_locales_ne_sont_jamais_un_serveur(string adresse, bool publique) =>
        EnvoisUdp.EstPublique(IPAddress.Parse(adresse)).Should().Be(publique);
}
