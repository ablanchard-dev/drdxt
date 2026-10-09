using System.Diagnostics;
using System.Net;
using DrDxt.Detection.Network;
using DrDxt.Detection.Wmi;
using DrDxt.Measurement.Network;
using FluentAssertions;
using Xunit.Abstractions;

namespace DrDxt.Detection.Tests.Network;

// 03/10/2026 (boucle 2D2P, P3.1d) : le diagnostic de route sur le vrai environnement, sans rien de simulé : le processus
// Call of Duty en cours, son vrai serveur (écoute ETW), les vrais routeurs (TTL), la vraie box. Sans partie ouverte ou sans
// droits d'administrateur, le test n'a rien à regarder et le dit. Avec, il exige un verdict et l'écrit dans la sortie.
public sealed class DiagnosticDeRouteSurLaVraiePartieTests(ITestOutputHelper sortie)
{
    [Fact]
    [Trait("Category", "Machine")] // une vraie partie de Call of Duty et le vrai réseau : 08/10/2026, il échouait dans la suite pendant une partie
    public async Task Sur_la_partie_en_cours_le_serveur_est_trouve_et_la_route_jugee()
    {
        var jeu = Process.GetProcessesByName("cod").FirstOrDefault();
        if (jeu is null) { sortie.WriteLine("aucune partie de Call of Duty ouverte : rien à regarder"); return; }

        var ecoute = new EcouteEnvoisUdp().Ecouter(TimeSpan.FromSeconds(4), jeu.Id);
        if (ecoute is null) { sortie.WriteLine("écoute impossible (droits)"); return; }
        var serveur = EnvoisUdp.ServeurDeLaPartie(ecoute, jeu.Id);
        sortie.WriteLine($"envois du jeu vus : {ecoute.Count} destinations ; serveur retenu : {serveur?.ToString() ?? "aucun"}");
        if (serveur is null) { sortie.WriteLine("le jeu n'envoie rien vers une adresse publique (menu ?)"); return; }

        var service = new DiagnosticDeRouteService(
            (pid, d) => new EcouteEnvoisUdp().Ecouter(d, pid),
            new NetworkLatencyMeter(new SystemPingProbe()),
            new AttributionReseauService(new NetworkAdapterReader(new ManagementWmiQuery()),
                new NetworkLatencyMeter(new SystemPingProbe())).Passerelle);

        var chemin = await service.RemonterLeCheminAsync(serveur.Address, CancellationToken.None);
        foreach (var s in chemin) sortie.WriteLine($"  rang {s.Ttl,2} : {s.Adresse?.ToString() ?? "*"} {(s.Ms is { } ms ? $"{ms:0} ms" : "")}");

        var constat = await service.MesurerAsync(jeu.Id, TimeSpan.FromSeconds(20));
        sortie.WriteLine($"verdict : {constat?.Statut} | {constat?.ValeurCourante} | {constat?.Explication}");

        constat.Should().NotBeNull("une partie ouverte qui envoie vers un serveur public doit donner une phrase");
        DiagnosticDeRoute.DernierSautQuiRepond(chemin).Should().NotBeNull("au moins un routeur au-delà de la box répond (tracert)");
    }
}
