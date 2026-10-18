using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Autopilot;

// Le banc vérifie la fréquence maximale d'une carte AMD par le levier du produit, avec une BAISSE de 50 MHz : jamais le
// profil d'overclock de l'App, jamais sur une autre carte. Le retour rend la fréquence d'origine relue.
public sealed class FrequenceAmdDuBancTests
{
    private readonly INvApiGpuTuning _nvApi = Substitute.For<INvApiGpuTuning>();
    private readonly IAdlxGpuTuning _adlx = Substitute.For<IAdlxGpuTuning>();
    private readonly IStabilityTester _stability = Substitute.For<IStabilityTester>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static HardwareInventory Hw(string modele, string fabricant) => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        [new GpuInfo(modele, fabricant, 16L << 30, "32.0", TriState.On)],
        [new MemoryModule("PN", "G.Skill", 6000, 6000)],
        new MotherboardInfo("ASUS", "B650"), new BiosInfo("1.2", null, true), ChassisType.Desktop);

    private FrequenceAmdDuBanc Build()
    {
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(0));
        _adlx.GetGfxMaxFreqMhz(Arg.Any<int>()).Returns(Result<int>.Ok(2900));
        _stability.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Stable)));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        // Le profil de l'App monte de 75 MHz : le banc ne doit jamais le prendre.
        var source = new GpuTuningTargetSource(new GpuTuningTarget(0, 75, 0), new GpuPowerLimitTarget(0, 90));
        source.SetAmdGpuPresenceFromHardware(Hw("AMD Radeon RX 7800 XT", "AMD"));
        return new FrequenceAmdDuBanc(new GpuUndervoltTweak(_nvApi, _adlx, _stability, _elevated, source));
    }

    [Fact]
    public async Task La_frequence_maximale_baisse_de_50_MHz_et_le_retour_rend_l_origine()
    {
        var banc = Build();

        var pose = await banc.ApplyAsync();
        pose.IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2900 - FrequenceAmdDuBanc.BaisseMhz),
            Arg.Any<CancellationToken>());
        await _elevated.DidNotReceive().ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2975), Arg.Any<CancellationToken>());

        (await banc.RevertAsync(pose.Value!)).IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2900), Arg.Any<CancellationToken>());
        banc.Id.Should().Be("gpu.undervolt", "le journal du banc retrouve le levier par son identifiant pour rendre l'essai");
    }

    [Fact]
    public void Seulement_sur_une_carte_AMD()
    {
        var banc = Build();
        banc.AppliesTo(Hw("AMD Radeon RX 7800 XT", "AMD"), State).Should().BeTrue();
        banc.AppliesTo(Hw("NVIDIA GeForce RTX 4070", "NVIDIA"), State).Should().BeFalse();
    }

    // Avec une NVIDIA à côté de la Radeon, le levier prendrait NVAPI : le test « ne fait rien sur une NVIDIA », il se dit
    // sans objet au lieu d'une écriture refusée.
    [Fact]
    public void Rien_sur_une_machine_qui_porte_aussi_une_NVIDIA()
    {
        var mixte = Hw("AMD Radeon RX 7800 XT", "AMD") with
        {
            Gpus = [new GpuInfo("AMD Radeon RX 7800 XT", "AMD", 16L << 30, "32.0", TriState.On),
                    new GpuInfo("NVIDIA GeForce RTX 4070", "NVIDIA", 12L << 30, "551.23", TriState.On)],
        };

        Build().AppliesTo(mixte, State).Should().BeFalse();
    }
}
