using System.Net;
using System.Net.Sockets;
using DrDxt.Detection.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Network;

// L'écoute des envois UDP, sur le vrai noyau. Le test envoie lui-même cinq datagrammes
// d'un octet vers 192.0.2.1:9 (plage réservée à la documentation, RFC 5737 : rien ne la route) et doit les retrouver,
// adresse, port et octets compris. Sans droits d'administrateur la session ne s'ouvre pas : l'écoute le dit (null).
public sealed class EcouteEnvoisUdpSurLeVraiSystemeTests
{
    [Fact, Trait("Category", "Machine")]
    public async Task Les_envois_de_ce_processus_sont_vus_avec_leur_destination()
    {
        var pid = Environment.ProcessId;
        var ecoute = Task.Run(() => new EcouteEnvoisUdp().Ecouter(TimeSpan.FromSeconds(3), pid));
        await Task.Delay(1000);
        using (var udp = new UdpClient())
            for (var i = 0; i < 5; i++) { await udp.SendAsync(new byte[] { 1 }, 1, "192.0.2.1", 9); await Task.Delay(50); }

        var envois = await ecoute;
        if (envois is null) return; // pas administrateur : l'écoute n'a pas regardé et le dit
        var vers = envois.Where(e => e.Destination.Equals(IPAddress.Parse("192.0.2.1")) && e.PortDestination == 9).ToList();
        vers.Should().ContainSingle("les cinq envois se cumulent sous une seule destination");
        vers[0].Pid.Should().Be(pid);
        vers[0].Octets.Should().Be(5);
        EnvoisUdp.ServeurDeLaPartie(envois, pid).Should().Be(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
    }
}
