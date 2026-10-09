using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

// 07/10/2026 (chantier D6) : le jeu passe-t-il par le Wi-Fi ?
public sealed class ConnexionDeJeuTests
{
    [Fact]
    public void Le_wifi_seul_se_dit_avec_le_geste()
    {
        var c = ConnexionDeJeu.Juger([new("Wi-Fi", true)]);
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.Explication.Should().Contain("Activision").And.Contain("ne change rien");
    }

    // Revue F-43 : câble et Wi-Fi reliés ensemble, la route décide ; sans elle, rien n'est affirmé.
    [Fact]
    public void Cable_et_wifi_relies_la_route_decide()
    {
        ConnexionDeJeu.Juger([new("Wi-Fi", true), new("Ethernet", false, PorteLaRoute: true)]).Statut.Should().Be(RepairStatus.ExpectedDefault);
        ConnexionDeJeu.Juger([new("Wi-Fi", true, PorteLaRoute: true), new("Ethernet", false)]).Statut.Should().Be(RepairStatus.Suspicious);
        ConnexionDeJeu.Juger([new("Wi-Fi", true), new("Ethernet", false)]).Statut.Should().Be(RepairStatus.Unknown);
    }

    // Revue F-43 : un VPN se dit « Ethernet » ; derrière lui, le Wi-Fi reste le Wi-Fi.
    [Fact]
    public void Un_vpn_ne_passe_pas_pour_un_cable()
    {
        ConnexionDeJeu.Juger([new("Wi-Fi", true), new("TAP-Windows Adapter V9", false, Virtuelle: true, PorteLaRoute: true)])
            .Statut.Should().Be(RepairStatus.Suspicious);
        ConnexionDeJeu.Juger([new("NordLynx", false, Virtuelle: true)]).Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void Non_lu_et_sans_sortie_ne_disent_rien_de_faux()
    {
        ConnexionDeJeu.Juger(null).Statut.Should().Be(RepairStatus.Unknown);
        ConnexionDeJeu.Juger([]).Statut.Should().Be(RepairStatus.Observed);
    }
}
