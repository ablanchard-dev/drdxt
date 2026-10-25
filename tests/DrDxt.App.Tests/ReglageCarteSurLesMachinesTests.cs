using DrDxt.App.Profiles;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Scoring;
using DrDxt.Profiles;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// La phrase « réglage de la carte ouvert / fermé » rejouée sur les machines enregistrées,
/// avec les VRAIS leviers de la carte et le vrai consommateur du scan. Les fixtures ne portent pas le détail des arrêts
/// (dates seules) : la phrase ne doit donc nommer aucune famille d'arrêt, et le portable ne se voit rien proposer
/// (`GpuGating`).
/// </summary>
public sealed class ReglageCarteSurLesMachinesTests
{
    private static ITweakCatalog Catalogue()
    {
        var nv = Substitute.For<INvApiGpuTuning>();
        var adlx = Substitute.For<IAdlxGpuTuning>();
        var elevated = Substitute.For<IElevatedClient>();
        var cibles = new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, 90));
        ITweak[] vrais = [new GpuUndervoltTweak(nv, adlx, Substitute.For<IStabilityTester>(), elevated, cibles),
                          new GpuPowerLimitTweak(nv, adlx, elevated, cibles)];
        var c = Substitute.For<ITweakCatalog>();
        c.Get(Arg.Any<string>()).Returns(_ => Result<ITweak>.Fail("TWEAK.NOT_FOUND", "absent du test"));
        foreach (var t in vrais) c.Get(t.Id).Returns(Result<ITweak>.Ok(t));
        c.All().Returns(vrais);
        return c;
    }

    [Theory]
    [MemberData(nameof(MachinesEnregistreesTests.Noms), MemberType = typeof(MachinesEnregistreesTests))]
    public async Task Chaque_machine_lit_une_phrase_vraie_sur_le_reglage_de_sa_carte(string nom)
    {
        var m = MachinesEnregistreesTests.Charger(nom);
        var consommateur = new ScanProfileConsumer(new ChipProfileMatcher(new ChipProfileCatalog()), Catalogue(),
            new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, 90)));
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(m.Inventaire));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(m.Reglages));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.WmiFail, "pas de mesure dans une fixture"));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, new ScanScorer(), sd, k => k, repairAudit: () => m.Sante,
            profileConsumer: consommateur);

        await vm.ScanCommand.ExecuteAsync(null);

        var phrase = vm.PhraseReglageCarte;
        phrase.Should().NotBeNullOrWhiteSpace();
        phrase.Should().NotContainAny("écran bleu", "veille", "arrêt sec", "fermé");
        if (m.Inventaire.Chassis == ChassisType.Laptop)
            phrase.Should().Be("Apply.Carte.NonProposee", "GpuGating : pas de réglage de la carte sur un portable");
        if (nom.StartsWith("reference", StringComparison.Ordinal))
            phrase.Should().StartWith("Réglage de la carte ouvert : le journal des arrêts n'a pas pu être lu",
                "RTX 4060 de bureau : la carte est proposée, et une fixture n'a pas lu le journal");
    }
}
