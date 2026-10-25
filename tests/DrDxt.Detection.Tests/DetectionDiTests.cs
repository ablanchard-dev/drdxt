using DrDxt.Core;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Detection.Gpu.Nvapi;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Network;
using NSubstitute;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Detection.Tests;

public class DetectionDiTests
{
    // Le cablage du verdict de cap doit rester VIVANT. FrameCapInferencer a existe une nuit
    // entiere -- teste, mute-teste -- sans qu'aucun code du produit ne l'appelle : un
    // diagnostic que le produit ne prononce pas ne sert a rien. Ces deux tests pinnent les
    // deux moities : il passe les seams quand ils existent, et il ne casse pas sans eux.

    [Fact]
    public async Task The_settings_reader_uses_the_driver_and_display_seams_when_they_are_registered()
    {
        var nvApi = Substitute.For<INvApi>();
        nvApi.GetFrameRateLimit().Returns(Result<uint>.Ok(141u));
        nvApi.GetFrameRateLimitOrigin().Returns(
            Result<DrsOriginState>.Ok(DrsSettingOrigin.Describe(0, 0, 0)));
        var display = Substitute.For<IDisplayInfo>();
        display.GetAllDisplays().Returns(Result<IReadOnlyList<DisplayInfo>>.Ok(new[]
        {
            new DisplayInfo(true, false, 200, 200, @"\.\DISPLAY1"),
            new DisplayInfo(false, false, 144, 144, @"\.\DISPLAY2"),
        }));
        var network = Substitute.For<INetworkAdapterReader>();
        network.Read().Returns(Result<IReadOnlyList<NetworkAdapterState>>.Fail(ErrorCodes.WmiFail, "non lu"));

        var provider = new ServiceCollection()
            .AddDrDxtDetection()
            .AddSingleton(nvApi)
            .AddSingleton(display)
            .AddSingleton(network)
            .BuildServiceProvider();

        var state = await provider.GetRequiredService<ISettingsStateReader>()
            .ReadAsync(TestHardware());

        state.Value!.FrameCap!.Value.Verdict.Should().Be(FrameCapVerdict.ComputedOnAnotherDisplay,
            "le verdict doit atteindre le produit, pas seulement une sonde");
    }

    [Fact]
    public void The_settings_reader_still_resolves_where_the_optional_seams_are_absent()
    {
        // L'injection .NET honore les parametres optionnels : ce test reste vert avec le cablage par convention comme
        // avec une fabrique explicite. Il existe pour que ca le reste : sans lui, ajouter un seam
        // obligatoire au lecteur casserait tout hote qui ne l'enregistre pas.
        var act = () => new ServiceCollection().AddDrDxtDetection().BuildServiceProvider()
            .GetRequiredService<ISettingsStateReader>();

        act.Should().NotThrow();
    }

    private static HardwareInventory TestHardware() => new(
        new CpuInfo("CPU", "AMD", 8, 16, true, 1),
        new[] { new GpuInfo("GPU", "NVIDIA", 12L << 30, "31", TriState.Unknown) },
        new[] { new MemoryModule("PN", "Corsair", 4800, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1.A0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    [Fact]
    public void Resolves_hardware_detector_and_settings_reader_and_scorer()
    {
        var provider = new ServiceCollection()
            .AddDrDxtDetection()
            .BuildServiceProvider();

        var hardwareDetector = provider.GetRequiredService<IHardwareDetector>();
        var settingsReader   = provider.GetRequiredService<ISettingsStateReader>();
        var scorer           = provider.GetRequiredService<IScanScorer>();

        hardwareDetector.Should().NotBeNull();
        settingsReader.Should().NotBeNull();
        scorer.Should().NotBeNull();

        hardwareDetector.Should().BeOfType<HardwareDetector>();
    }
}

// -----------------------------------------------------------------------
// Le conteneur de detection doit lire les ecrans.
//
// Le constat de consommation au repos naissait mort partout sauf dans l'application WPF :
// `IDisplayInfo` n'etait enregistre que par la couche App, donc tout ce qui monte
// `AddDrDxtDetection()` -- les sondes, les tests d'integration, un futur mode console --
// lisait `AttachedRefreshHz = null` et se taisait. La sonde affirmait pourtant « la meme
// chaine que l'application » : elle mentait par omission, et ca m'a fait attribuer le
// silence a la sonde au lieu du cablage.
// -----------------------------------------------------------------------
public class DetectionReadsDisplaysTests
{
    [Fact]
    public void Le_conteneur_de_detection_fournit_un_lecteur_d_ecrans()
    {
        using var sp = new ServiceCollection().AddDrDxtDetection().BuildServiceProvider();

        sp.GetService<DrDxt.Application.Tweaks.Hardware.IDisplayInfo>()
            .Should().NotBeNull("sans lui, tout ce qui n'est pas l'application WPF est aveugle aux ecrans");
    }
}
