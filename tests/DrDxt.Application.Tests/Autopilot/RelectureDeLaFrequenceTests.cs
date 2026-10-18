using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Autopilot;

// La fréquence maximale posée par le banc se relit sur la carte (ADLX), face à l'origine gardée dans son inverse :
// une autre valeur que l'origine = posée ; l'origine = rendue ; illisible = non conclu.
public sealed class RelectureDeLaFrequenceTests
{
    private readonly IAdlxGpuTuning _carte = Substitute.For<IAdlxGpuTuning>();

    private ValidationDeLaMachine Machine() => new(Substitute.For<ITweakCatalog>(), Substitute.For<IHardwareDetector>(),
        Substitute.For<ISettingsStateReader>(), Substitute.For<IRegistryReader>(), Substitute.For<INvApi>(),
        Substitute.For<IPowerPlanReader>(), _carte, Substitute.For<IAdlx>(), Substitute.For<INvApiGpuTuning>());

    private static TweakSnapshot Origine2900()
        => new("gpu.undervolt", PrivilegedOpCodec.Serialize(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2900)).Value!);

    [Theory]
    [InlineData(2850, true)]
    [InlineData(2900, false)]
    public void La_frequence_lue_sur_la_carte_dit_si_la_baisse_est_en_place(int lue, bool posee)
    {
        _carte.GetGfxMaxFreqMhz(0).Returns(Result<int>.Ok(lue));

        Machine().Relire(Origine2900()).Pose.Should().Be(posee);
    }

    [Fact]
    public void Illisible_ne_conclut_rien()
    {
        _carte.GetGfxMaxFreqMhz(0).Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "pas de pilote"));

        Machine().Relire(Origine2900()).Pose.Should().BeNull();
    }
}
