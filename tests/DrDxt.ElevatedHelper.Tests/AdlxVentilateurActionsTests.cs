using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.GpuNative.Nvml;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>Le ventilateur AMD en mode Expert, côté helper : un faux ADLX, aucune vraie carte touchée. La même vitesse sur
/// chaque état de la courbe, jamais sous le plancher à chaud ; un échec rend la courbe de la carte.</summary>
public sealed class AdlxVentilateurActionsTests
{
    private readonly IAdlxNative _adlx = Substitute.For<IAdlxNative>();

    public AdlxVentilateurActionsTests() => _adlx.IsAvailable.Returns(true);

    [Fact]
    public void Une_vitesse_dans_la_bande_se_pose()
    {
        _adlx.PoserVentilateurFixe(60).Returns((string?)null);

        new AdlxVentilateurActions(_adlx).Poser(0, 60).IsOk.Should().BeTrue();
    }

    [Theory]
    [InlineData(39)]
    [InlineData(101)]
    public void Hors_de_la_bande_rien_ne_s_ecrit(int pourcent)
    {
        new AdlxVentilateurActions(_adlx).Poser(0, pourcent).IsOk.Should().BeFalse();
        _adlx.DidNotReceive().PoserVentilateurFixe(Arg.Any<int>());
    }

    [Fact]
    public void Une_pose_refusee_rend_la_courbe_de_la_carte_et_le_dit()
    {
        _adlx.PoserVentilateurFixe(60).Returns("The fan speed read back is not the one written.");
        _adlx.RendreVentilateurAuto().Returns((string?)null);

        var r = new AdlxVentilateurActions(_adlx).Poser(0, 60);

        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        _adlx.Received(1).RendreVentilateurAuto();
    }

    [Fact]
    public void Un_retour_qui_ne_se_relit_pas_est_dit_partiel()
    {
        _adlx.RendreVentilateurAuto().Returns("The fan curve read back is not the card's default.");

        new AdlxVentilateurActions(_adlx).Rendre(0).Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
    }

    [Fact]
    public void La_carte_NVIDIA_garde_NVML_sinon_ADLX_sert()
    {
        var nvidia = Substitute.For<IVentilateurActions>();
        var amd = Substitute.For<IVentilateurActions>();
        var nvmlPresent = true;
        var route = new VentilateurSelonLaCarte(nvidia, amd, () => nvmlPresent);

        route.Poser(0, 60);
        nvmlPresent = false;
        route.Rendre(0);

        nvidia.Received(1).Poser(0, 60);
        amd.Received(1).Rendre(0);
    }

    // La garde à 80 °C : sans NVML, la température vient d'ADLX ; sinon une vitesse fixe AMD serait rendue en 5 s, faute de
    // température lue.
    [Fact]
    public void Sans_NVML_la_garde_lit_la_temperature_d_ADLX()
    {
        var nvml = Substitute.For<INvmlNative>();
        nvml.IsAvailable.Returns(false);
        _adlx.LireTemperatureCarte().Returns(61.4);

        GardeThermiqueVentilateur.Temperature(nvml, _adlx)(0).Should().Be(61u);
    }
}
