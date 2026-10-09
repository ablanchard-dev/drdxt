using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// NET1 (08/10/2026) : la qualité du Wi-Fi, lue sans la localisation (wlan_intf_opcode_realtime_connection_quality et
/// wlan_intf_opcode_channel_number, absents de la liste que Microsoft soumet à l'accord de localisation). Lecture seule.
/// </summary>
public sealed class QualiteWifiTests
{
    [Theory]
    [InlineData(2437, "2,4 GHz")]
    [InlineData(5180, "5 GHz")]
    [InlineData(5955, "6 GHz")]
    [InlineData(5_180_000, "5 GHz")] // Microsoft documente le champ en kHz malgré son nom
    public void La_bande_vient_de_la_frequence(int frequence, string bande) =>
        new QualiteWifi(10, 80, -60, frequence, 36).Bande.Should().Be(bande);

    [Theory]
    [InlineData(7, "Wi-Fi 4")]
    [InlineData(8, "Wi-Fi 5")]
    [InlineData(10, "Wi-Fi 6")]
    [InlineData(11, "Wi-Fi 7")]
    [InlineData(6, null)]
    public void La_generation_vient_du_type_de_PHY(int phy, string? generation) =>
        new QualiteWifi(phy, 80, null, null, null).Generation.Should().Be(generation);

    [Fact]
    public void La_phrase_dit_ce_qui_est_lu_et_rien_d_autre()
    {
        new QualiteWifi(10, 45, -77, 2437, 6).Phrase().Should().Be("signal 45 % (-77 dBm), 2,4 GHz canal 6, Wi-Fi 6");
        new QualiteWifi(0, 70, null, null, null).Phrase().Should().Be("signal 70 %");
    }

    private static readonly CarteVersInternet Wifi = new("Wi-Fi", SansFil: true, PorteLaRoute: true);

    [Fact]
    public void Le_constat_Wi_Fi_porte_la_qualite_lue()
    {
        var f = ConnexionDeJeu.Juger([Wifi], new QualiteWifi(10, 45, -77, 2437, 6));
        f.Explication.Should().Contain("signal 45 % (-77 dBm), 2,4 GHz canal 6, Wi-Fi 6");
    }

    [Fact]
    public void Sans_qualite_lue_le_constat_reste_celui_d_avant()
    {
        var f = ConnexionDeJeu.Juger([Wifi]);
        f.Explication.Should().NotContain("signal");
        f.Statut.Should().Be(RepairStatus.Suspicious);
    }
}
