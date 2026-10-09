using System.Net;
using DrDxt.Core;
using DrDxt.Detection.Network;
using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Network;

// 03/10/2026 (boucle 2D2P, P3.1c) : du serveur repéré à la phrase du domaine Réseau. Les sondes sont simulées : la route
// vers le serveur et la box rendent ce qu'on leur dit, et le service doit en tirer le bon verdict, ou se taire.
public sealed class DiagnosticDeRouteServiceTests
{
    private const int Jeu = 4242;
    private static readonly IReadOnlyList<EnvoiUdp> UnServeur = [new(Jeu, 5000, IPAddress.Parse("185.34.107.20"), 3074)];

    private sealed class Sondes(Func<string, NetworkLatencyReport> rendre) : INetworkLatencyMeter
    {
        public List<string> Cibles { get; } = [];
        public Task<Result<NetworkLatencyReport>> MeasureAsync(string host, int probeCount, TimeSpan interval, TimeSpan timeout,
            CancellationToken ct, int warmupProbes = 0)
        {
            Cibles.Add(host);
            return Task.FromResult(Result<NetworkLatencyReport>.Ok(rendre(host)));
        }
    }

    private static NetworkLatencyReport R(string cible, double ping, double gigue, int recus = 80, double perte = 0) =>
        new(cible, 80, recus, perte, recus == 0 ? null : ping, ping, ping, ping, recus == 0 ? null : gigue);

    /// <summary>Le chemin simulé : rang 1 la box, rangs 2 et 3 des routeurs, puis plus rien (le serveur et la suite se taisent).</summary>
    private sealed class Chemin : ISondeDeSaut
    {
        public List<int> Rangs { get; } = [];
        public Task<(IPAddress? Adresse, double? Ms)> SonderAsync(IPAddress cible, int ttl, TimeSpan delai, CancellationToken ct)
        {
            Rangs.Add(ttl);
            return Task.FromResult<(IPAddress?, double?)>(ttl switch
            {
                1 => (IPAddress.Parse("192.168.1.254"), 1),
                2 => (IPAddress.Parse("87.89.128.2"), 4),
                3 => (IPAddress.Parse("62.34.3.217"), 9),
                _ => (null, null),
            });
        }
    }

    private static DiagnosticDeRouteService Service(Sondes s, IReadOnlyList<EnvoiUdp>? envois = null, string? box = "192.168.1.254",
        ISondeDeSaut? chemin = null) =>
        new((_, _) => envois ?? UnServeur, s, () => box, chemin ?? new Chemin());

    [Fact]
    public async Task Une_route_courte_et_stable_dit_qu_un_relais_ne_sert_a_rien()
    {
        var s = new Sondes(c => c == "192.168.1.254" ? R(c, 1, 0.5) : R(c, 14, 1));
        var constat = await Service(s).MesurerAsync(Jeu, TimeSpan.FromSeconds(20));

        constat!.Statut.Should().Be(RepairStatus.ExpectedDefault);
        constat.Explication.Should().Contain("185.34.107.20").And.Contain("n'a rien à gagner");
        s.Cibles.Distinct().Should().BeEquivalentTo(["185.34.107.20", "192.168.1.254"], "seuls le serveur du jeu et la box sont sondés");
    }

    [Fact]
    public async Task Un_serveur_et_un_chemin_muets_ne_font_rien_affirmer()
    {
        var s = new Sondes(c => R(c, 0, 0, recus: 0, perte: 100));
        var constat = await Service(s, chemin: new CheminMuet()).MesurerAsync(Jeu, TimeSpan.FromSeconds(20));

        constat!.Statut.Should().Be(RepairStatus.Observed);
        constat.Explication.Should().Contain("ne répond pas").And.Contain("rien n'est affirmé");
    }

    private sealed class CheminMuet : ISondeDeSaut
    {
        public Task<(IPAddress? Adresse, double? Ms)> SonderAsync(IPAddress cible, int ttl, TimeSpan delai, CancellationToken ct) =>
            Task.FromResult<(IPAddress?, double?)>(ttl == 1 ? (IPAddress.Parse("192.168.1.254"), 1) : (null, null));
    }

    // 03/10/2026 (P3.1d, test en vraie partie) : le serveur de Call of Duty ignore le ping. La route est jugée jusqu'au
    // dernier routeur qui répond, la phrase dit où la mesure s'arrête, et la remontée s'arrête après trois rangs muets.
    [Fact]
    public async Task Un_serveur_qui_ignore_le_ping_est_juge_jusqu_au_dernier_routeur_qui_repond()
    {
        var s = new Sondes(c => c == "185.34.107.20" ? R(c, 0, 0, recus: 0, perte: 100) : R(c, 1, 0));
        var chemin = new Chemin();
        var constat = await Service(s, chemin: chemin).MesurerAsync(Jeu, TimeSpan.FromSeconds(20));

        constat!.Explication.Should().Contain("routeur n° 3 (62.34.3.217)").And.Contain("ignore le ping. Ta route").And.Contain("n'a rien à gagner");
        s.Cibles.Should().NotContain("62.34.3.217", "un routeur qui ignore le ping direct est mesuré à son rang, pas par un ping direct");
        chemin.Rangs.Take(6).Should().Equal(1, 2, 3, 4, 5, 6);
        chemin.Rangs.Skip(6).Should().OnlyContain(r => r == 3).And.HaveCountGreaterThanOrEqualTo(60, "la mesure complète sonde au rang 3");
    }

    [Fact]
    public async Task Sans_envoi_vers_une_adresse_publique_il_n_y_a_rien_a_dire()
    {
        var s = new Sondes(c => R(c, 14, 1));
        (await Service(s, envois: [new(Jeu, 5000, IPAddress.Parse("192.168.1.254"), 3074)]).MesurerAsync(Jeu, TimeSpan.FromSeconds(20)))
            .Should().BeNull();
        s.Cibles.Should().BeEmpty("aucune sonde sans serveur repéré");
    }

    [Fact]
    public async Task Une_ecoute_impossible_ne_devient_pas_un_verdict()
    {
        var s = new Sondes(c => R(c, 14, 1));
        (await new DiagnosticDeRouteService((_, _) => null, s, () => null).MesurerAsync(Jeu, TimeSpan.FromSeconds(20))).Should().BeNull();
    }
}
