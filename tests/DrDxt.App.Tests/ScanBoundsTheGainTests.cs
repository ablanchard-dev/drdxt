using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Le plafond du gain, jusqu'a l'ecran.
//
// `PlafondGpu` etait le type le plus oriente GAIN du depot et personne ne l'appelait — ni le
// produit, ni meme un outil : seulement ses propres tests. Il repond pourtant a la question qui
// fait perdre le plus de temps a un joueur : « combien puis-je gagner au plus en liberant du
// processeur ? » Sur la machine de reference, la reponse mesuree a empeche d'annoncer +25 a +30 %.
//
// Ces tests portent sur ce que l'ecran dit, et surtout sur ce qu'il refuse de dire.
// -----------------------------------------------------------------------
public sealed class ScanBoundsTheGainTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
        new[] { new GpuInfo("NVIDIA RTX 4060", "NVIDIA", 8L << 30, "551", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3800, 4000) },
        new MotherboardInfo("Gigabyte", "B760"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    /// <param name="gpuBusyMs">null = le scan n'a mesure aucun GPU.</param>
    /// <param name="mesureRefusee">true = pas de jeu lance, scan degrade.</param>
    private static ScanViewModel Build(double? gpuBusyMs, bool mesureRefusee = false)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        // Des trames de 5 ms : avec 4,5 ms de GPU occupe, l'occupation vaut 90 %.
        var bl = new BenchmarkResult(200, 150, 120, new List<double> { 5.0, 5.0, 5.0, 5.0 },
            null, 1.0, 5, MeanGpuBusyMs: gpuBusyMs);
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(mesureRefusee
              ? Result<BenchmarkResult>.Fail(ErrorCodes.MeasureNoSamples, "aucun jeu")
              : Result<BenchmarkResult>.Ok(bl));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St, new List<Finding>(), 68, bl,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        var store = Substitute.For<IDesiredStateStore>();
        store.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Ok());
        store.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));

        return new ScanViewModel(
            hd, sr, br, sc, sd,
            ScanViewModelTestSeams.Safety(), ScanViewModelTestSeams.Engine(),
            ScanViewModelTestSeams.ProfileConsumer(), ScanViewModelTestSeams.GpuTuningStep(),
            ScanViewModelTestSeams.Navigation(), ScanViewModelTestSeams.DashboardFactory(),
            store, new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key);
    }

    [Fact]
    public async Task Le_scan_dit_le_PLAFOND_du_gain_et_pas_une_prediction()
    {
        var vm = Build(gpuBusyMs: 4.5);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.GpuCeilingLabel.Should().NotBeNull();
        vm.GpuCeilingLabel.Should().Contain("90").And.Contain("11,1");
        vm.GpuCeilingLabel.Should().Contain("tu ne gagnerais au mieux que",
            "« pas plus que X » ; le jour ou cette ligne dit « X », elle promet");
        vm.GpuCeilingLabel.Should().Contain("dépend de la scène",
            "une borne lue sans son rappel de scene redevient une promesse");
        vm.GpuCeilingLabel.Should().NotContain("..", "27/09 : « en pratique moins.. Cette borne » a l'ecran");
    }

    [Fact]
    public async Task La_borne_ne_publie_AUCUN_chiffre_mesure_sur_une_AUTRE_machine()
    {
        // Les series de reference vont de +5,3 % a +16,3 % — sur une machine et un jeu. Les
        // afficher chez quelqu'un d'autre ferait passer la mesure d'un tiers pour la sienne.
        var vm = Build(gpuBusyMs: 4.5);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.GpuCeilingLabel.Should().NotContain("5,3").And.NotContain("16,3");
    }

    [Fact]
    public async Task Sans_GPU_mesure_l_ecran_ne_promet_RIEN()
    {
        var vm = Build(gpuBusyMs: null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.GpuCeilingLabel.Should().BeNull();
    }

    [Fact]
    public async Task Un_scan_degrade_ne_fabrique_pas_de_plafond()
    {
        // Pas de jeu lance : le banc refuse, et il n'y a aucune trame sur quoi borner.
        var vm = Build(gpuBusyMs: 4.5, mesureRefusee: true);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.GpuCeilingLabel.Should().BeNull();
    }
}
