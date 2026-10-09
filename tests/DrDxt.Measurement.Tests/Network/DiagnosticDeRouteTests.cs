using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Network;

// 03/10/2026 (boucle 2D2P, P3.1) : dire quand un relais payant (ExitLag, GearUP…) peut servir, et surtout quand il ne sert à rien.
public sealed class DiagnosticDeRouteTests
{
    private static MesureDeRoute M(double ping, double perte = 0, double gigue = 1, int n = 120,
        NetworkPathVerdict attribution = NetworkPathVerdict.Indistinguishable) => new(n, ping, perte, gigue, attribution);

    [Fact]
    public void Trop_peu_de_mesures_ne_disent_rien() =>
        DiagnosticDeRoute.Juger(M(80, perte: 5, n: 59)).Should().Be(VerdictRoute.Inconnu);

    [Fact]
    public void Une_instabilite_qui_commence_a_la_box_n_est_pas_l_affaire_d_un_relais() =>
        DiagnosticDeRoute.Juger(M(70, perte: 4, gigue: 20, attribution: NetworkPathVerdict.StartsAtFirstHop))
            .Should().Be(VerdictRoute.ProblemeLocal, "le relais passe par le même lien local");

    [Theory]
    [InlineData(12)]
    [InlineData(30)]
    public void Un_ping_bas_et_stable_n_a_rien_a_gagner(double ping) =>
        DiagnosticDeRoute.Juger(M(ping)).Should().Be(VerdictRoute.RouteDejaBonne);

    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(0.0, 8.1)]
    public void Des_pertes_ou_des_a_coups_au_dela_de_la_box_sont_le_cas_ou_un_relais_aide(double perte, double gigue) =>
        DiagnosticDeRoute.Juger(M(20, perte, gigue, attribution: NetworkPathVerdict.AppearsBeyondGateway))
            .Should().Be(VerdictRoute.RelaisPeutAider);

    [Fact]
    public void Une_route_longue_mais_stable_se_juge_par_un_essai_compare() =>
        DiagnosticDeRoute.Juger(M(31)).Should().Be(VerdictRoute.EssaiAComparer);

    // 03/10/2026 (P3.1d) : la box ne compte pas comme dernier routeur ; une route mesurée jusqu'à elle ne dit rien d'Internet.
    [Fact]
    public void La_box_seule_n_est_pas_un_dernier_routeur()
    {
        var box = new SautDeRoute(1, System.Net.IPAddress.Parse("192.168.1.254"), 1);
        DiagnosticDeRoute.DernierSautQuiRepond([box, new SautDeRoute(2, null, null)]).Should().BeNull();
        DiagnosticDeRoute.DernierSautQuiRepond([box, new SautDeRoute(2, System.Net.IPAddress.Parse("87.89.128.2"), 4), new SautDeRoute(3, null, null)])!
            .Ttl.Should().Be(2);
    }
}
