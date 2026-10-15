using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// Le jugement d'Optimiser est rendu dès la fin de l'analyse, pas au clic : sous « 0 à changer », la
// phrase dit ce que DrDXT voit, et le bouton reste.
public sealed class OptimiserNePromettePasDansLeVideTests
{
    [Fact]
    public async Task Sans_rien_a_poser_la_phrase_le_dit_et_le_bouton_reste()
    {
        var vm = Construire();

        await vm.ScanCommand.ExecuteAsync(null);

        vm.RienAPoserSeul.Should().BeTrue();
        vm.LabelRienAPoserSeul.Should().NotBeNullOrEmpty();
        vm.CanApply.Should().BeTrue("le joueur garde la main : Optimiser reste cliquable");
    }

    [Fact]
    public void Le_bouton_Optimiser_n_est_jamais_cache_par_la_vue()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var debut = xaml.IndexOf("x:Name=\"BoutonOptimiser\"", StringComparison.Ordinal);
        var bouton = xaml[debut..xaml.IndexOf("/>", debut, StringComparison.Ordinal)];

        bouton.Should().NotContain("Visibility=");
    }

    private static ScanViewModel Construire()
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
        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k);
    }
}
