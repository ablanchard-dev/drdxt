using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Autopilot;

// L'App et le banc vérifient par ValidationDeLaMachine : c'est elle qui lit l'essai en cours et le passe à la vérification.
// Sans ce branchement, le levier de l'essai serait posé puis rendu en plein essai, et le test du seam resterait vert.
public sealed class ValidationDeLaMachineEssaiTests : IDisposable
{
    private readonly string _journal = Path.Combine(Path.GetTempPath(), $"drdxt-vm-essai-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_journal); } catch (IOException) { } }

    private static readonly HardwareInventory Pc = new(new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
        [new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "32.0.16.1742", TriState.On)],
        [new MemoryModule("PN", "K", 3200, 3200)], new MotherboardInfo("M", "B"), new BiosInfo("1", null, true), ChassisType.Desktop);

    private async Task<ITweak> VerifierAvec(Func<string?> levierDeLEssai)
    {
        var limite = Substitute.For<ITweak>();
        limite.Id.Returns("gpu.power_limit");
        limite.AppliesTo(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>()).Returns(true);
        limite.ApplyAsync().Returns(Result<TweakSnapshot>.Fail("REFUS", "témoin"));
        var catalogue = Substitute.For<ITweakCatalog>();
        catalogue.Get(Arg.Any<string>()).Returns(Result<ITweak>.Fail("ABSENT", "hors du test"));
        catalogue.Get("gpu.power_limit").Returns(Result<ITweak>.Ok(limite));
        var detecteur = Substitute.For<IHardwareDetector>();
        detecteur.DetectAsync().Returns(Result<HardwareInventory>.Ok(Pc));
        var etat = Substitute.For<ISettingsStateReader>();
        etat.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(
            new SettingsState(TriState.On, TriState.On, "x", TriState.On, TriState.On, [], TriState.On)));

        await new ValidationDeLaMachine(catalogue, detecteur, etat, Substitute.For<IRegistryReader>(), Substitute.For<INvApi>(),
                Substitute.For<IPowerPlanReader>(), Substitute.For<IAdlxGpuTuning>(), Substitute.For<IAdlx>(),
                Substitute.For<INvApiGpuTuning>(), levierDeLEssai)
            .ValiderAsync(new FichierRevertEnAttente(_journal));
        return limite;
    }

    [Fact]
    public async Task Le_levier_de_l_essai_en_cours_n_est_jamais_pose()
    {
        var limite = await VerifierAvec(() => "gpu.power_limit");

        await limite.DidNotReceive().ApplyAsync();
    }

    [Fact]
    public async Task Sans_essai_en_cours_le_meme_levier_est_verifie()
    {
        var limite = await VerifierAvec(() => null);

        await limite.Received(1).ApplyAsync();
    }
}
