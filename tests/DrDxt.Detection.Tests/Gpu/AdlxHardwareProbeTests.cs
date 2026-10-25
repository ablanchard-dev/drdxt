using DrDxt.Core;
using DrDxt.Detection;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Detection.Tests.Gpu;

// La télémétrie de la carte en partie sur une Radeon : la sonde ADLX rend ce que NVML rend sur une GeForce (puissance,
// température, fréquence), plus le point chaud et le ventilateur. ADLX ne publie pas de raisons de bridage : non lues.
public sealed class AdlxHardwareProbeTests
{
    private static IAdlxNative Adlx(AdlxMetriques m)
    {
        var adlx = Substitute.For<IAdlxNative>();
        adlx.LireMetriques().Returns(m);
        return adlx;
    }

    [Fact]
    public void La_sonde_ADLX_rend_les_metriques_de_la_carte_dediee()
    {
        var lu = new AdlxHardwareProbe(Adlx(new AdlxMetriques(PuissanceW: 231, TemperatureC: 58, PointChaudC: 74,
            FrequenceMhz: 2480, VentilateurTrMin: 1450))).Lire();

        lu.GpuPowerW.Should().Be(231);
        lu.GpuTemperatureC.Should().Be(58);
        lu.GpuClockMhz.Should().Be(2480);
        lu.GpuPointChaudC.Should().Be(74);
        lu.VentilateurTrMin.Should().Be(1450);
        lu.Raisons.Should().BeNull("ADLX ne publie pas de raisons de bridage : non lues, jamais « rien ne bride »");
    }

    [Fact]
    public void Rien_lu_rend_des_null_jamais_des_zeros()
    {
        var lu = new AdlxHardwareProbe(Adlx(AdlxMetriques.RienLu("ADLX absent"))).Lire();
        lu.Should().Be(new HardwareReading(null, null));
    }

    [Fact]
    public void Une_sonde_qui_leve_rend_des_null()
    {
        var adlx = Substitute.For<IAdlxNative>();
        adlx.LireMetriques().Returns(_ => throw new InvalidOperationException("pilote"));
        new AdlxHardwareProbe(adlx).Lire().Should().Be(new HardwareReading(null, null));
    }

    // La sonde suit la marque de la carte dédiée : une Radeon par ADLX, le reste par NVML (une carte non lue aussi : le
    // comportement d'avant, NVML rend des null sans carte NVIDIA).
    [Theory]
    [InlineData("AMD", typeof(AdlxHardwareProbe))]
    [InlineData("Advanced Micro Devices, Inc.", typeof(AdlxHardwareProbe))]
    [InlineData("NVIDIA", typeof(NvmlHardwareProbe))]
    [InlineData("Intel", typeof(NvmlHardwareProbe))]
    [InlineData(null, typeof(NvmlHardwareProbe))]
    public void La_sonde_suit_la_marque_de_la_carte_dediee(string? constructeur, Type attendu)
    {
        var carte = constructeur is null ? null : new GpuInfo("Carte", constructeur, 8L << 30, "1", TriState.On);
        SondeDeLaCarte.Pour(carte, nvmlRepond: () => false, adlxPresent: () => false).Should().BeOfType(attendu);
    }

    // Sans inventaire (le relevé de fond, avant tout scan), la bibliothèque qui répond choisit : NVML d'abord, puis ADLX.
    [Theory]
    [InlineData(true, true, typeof(NvmlHardwareProbe))]
    [InlineData(false, true, typeof(AdlxHardwareProbe))]
    [InlineData(false, false, typeof(NvmlHardwareProbe))]
    public void Sans_carte_lue_la_bibliotheque_qui_repond_choisit(bool nvml, bool adlx, Type attendu)
        => SondeDeLaCarte.Pour(null, () => nvml, () => adlx).Should().BeOfType(attendu);
}
