using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// L'analyse dure plus de 10 s. Au-delà, un rond qui tourne sans fin fait croire à un plantage
// (NN/g, Progress Indicators) : elle avance par étapes nommées, sans jamais reculer, jusqu'à 100 %.
public sealed class LAnalyseAvanceParEtapesTests
{
    [Fact]
    public async Task L_analyse_avance_par_huit_etapes_nommees_jusqu_a_cent_pour_cent()
    {
        var hw = new HardwareInventory(
            new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
            new[] { new GpuInfo("NVIDIA RTX 4060", "NVIDIA", 8L << 30, "551", TriState.On) },
            new[] { new MemoryModule("PN", "Kingston", 3800, 4000) },
            new MotherboardInfo("MSI", "B760"), new BiosInfo("1", DateTimeOffset.UtcNow, true), ChassisType.Desktop);
        var st = new SettingsState(TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(hw));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(st));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.PidInvalid, "aucun jeu"));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(hw, st, new List<Finding>(), null)));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k == "Scan.Etape.Format" ? "{0}/{1} {2}" : k);
        var valeurs = new List<double>();
        var etapes = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ProgressionDuScan)) valeurs.Add(vm.ProgressionDuScan);
            if (e.PropertyName == nameof(vm.LabelEtapeDuScan)) etapes.Add(vm.LabelEtapeDuScan);
        };

        await vm.ScanCommand.ExecuteAsync(null);

        etapes.Should().Equal(
            "1/8 Scan.Etape.Materiel", "2/8 Scan.Etape.Reglages", "3/8 Scan.Etape.Bios", "4/8 Scan.Etape.ReseauProcesseur",
            "5/8 Scan.Etape.Memoire", "6/8 Scan.Etape.EnJeu", "7/8 Scan.Etape.Analyse", "8/8 Scan.Etape.Audit");
        valeurs.Should().BeInAscendingOrder("une progression ne recule jamais pendant un scan");
        valeurs[^1].Should().Be(1);
        vm.ProgressionDuScan.Should().Be(1);
    }
}
