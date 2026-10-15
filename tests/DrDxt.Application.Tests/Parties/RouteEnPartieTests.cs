using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Le ping pendant chaque partie, et la route qui se dégrade d'une partie à l'autre.
public sealed class RouteEnPartieTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    private static PartieRelevee P(int jour, double? net, double? box = 2)
        => new("cod.exe", T.AddDays(jour), T.AddDays(jour).AddMinutes(30), 4, 140, 90, BottleneckState.CpuBoundOrAppBound,
            PingInternetMedian: net, PingBoxMedian: box);

    [Fact]
    public void Trois_parties_toutes_au_dessus_des_trois_d_avant_et_la_box_stable_disent_au_dela_de_la_box()
    {
        var r = ReleveDesParties.RouteQuiSeDegrade([P(1, 12), P(2, 13), P(3, 12), P(4, 25), P(5, 27), P(6, 26)]);

        r.Should().Be(new ReleveDesParties.RouteDegradee(12, 26, BoxAussi: false));
    }

    [Fact]
    public void La_box_qui_monte_aussi_dit_le_lien_jusqu_a_elle()
        => ReleveDesParties.RouteQuiSeDegrade([P(1, 12, 2), P(2, 13, 2), P(3, 12, 3), P(4, 25, 9), P(5, 27, 10), P(6, 26, 8)])!
            .BoxAussi.Should().BeTrue();

    [Fact]
    public void Un_chevauchement_trop_peu_de_parties_ou_un_ping_non_lu_ne_disent_rien()
    {
        ReleveDesParties.RouteQuiSeDegrade([P(1, 12), P(2, 30), P(3, 12), P(4, 25), P(5, 27), P(6, 26)]).Should().BeNull();
        ReleveDesParties.RouteQuiSeDegrade([P(2, 13), P(3, 12), P(4, 25), P(5, 27), P(6, 26)]).Should().BeNull();
        ReleveDesParties.RouteQuiSeDegrade([P(0, null), P(2, 13), P(3, 12), P(4, 25), P(5, 27), P(6, 26)]).Should().BeNull();
    }

    [Fact]
    public void La_partie_garde_la_mediane_des_pings_et_le_pic_des_pertes_meme_a_zero()
    {
        CaptureEnPartie C(double? box, double? net, double? pertes) =>
            new(T, 140, 90, BottleneckState.CpuBoundOrAppBound, PingBox: box, PingInternet: net, PertesInternet: pertes);

        var p = ReleveDesParties.Resumer("cod.exe", [C(2, 12, 0), C(3, 14, 0), C(null, 13, 0)])!;
        p.PingBoxMedian.Should().Be(2.5);
        p.PingInternetMedian.Should().Be(13);
        p.PertesInternetMax.Should().Be(0, "zéro perte est une mesure, pas une absence");
        ReleveDesParties.Resumer("cod.exe", [C(null, null, null), C(null, null, null)])!.PertesInternetMax.Should().BeNull();
    }

    [Fact]
    public async Task La_veille_mesure_le_reseau_a_chaque_capture()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), null]);
        var mesures = new Queue<(double?, double?, double?)>([(2.0, 12.0, 0.0), (3.0, 14.0, 10.0)]);
        var enregistrees = new List<PartieRelevee>();
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(new BenchmarkResult(140, 98, 70, [], null, 1, 1)),
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: p => { enregistrees.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask,
            reseau: _ => Task.FromResult(mesures.Dequeue()));

        await veille.VeillerAsync(fin.Token);

        enregistrees[0].PingInternetMedian.Should().Be(13);
        enregistrees[0].PertesInternetMax.Should().Be(10);
    }
}
