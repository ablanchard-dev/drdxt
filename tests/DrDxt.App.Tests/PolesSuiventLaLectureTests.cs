using DrDxt.App.Effets;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// 29/09/2026 (boucle « vivant et prouvé », B1) : pendant le scan, le réseau tournait en boucle, décoratif. Chaque pôle
// s'allume maintenant quand son domaine a réellement été lu : processeur, mémoire, carte, puis stockage et Windows.
public sealed class PolesSuiventLaLectureTests
{
    [Fact]
    public async Task Les_poles_s_allument_dans_l_ordre_reel_de_la_lecture()
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
          .Returns(Result<ScanResult>.Ok(new ScanResult(hw, st, new List<Finding>(), 68, null,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k);
        var vus = new List<int>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.PolesLus)) vus.Add(vm.PolesLus); };

        await vm.ScanCommand.ExecuteAsync(null);

        // 10/10/2026 : huit satellites. Processeur (bit 0), mémoire (bit 3), carte (bit 1), puis le reste du cercle.
        vus.Should().Equal(0b00000001, 0b00001001, 0b00001011, 0b11111111);
    }
}
