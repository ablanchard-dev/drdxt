using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// « L'IMAGE EST PRETE ET ON ATTEND » — le blocage de presentation, jusqu'a l'ecran.
//
// C'est la seule des grandeurs mesurees sur laquelle un joueur peut agir seul : un cap d'images,
// un vsync ou le compositeur se changent dans les reglages du jeu. Elle etait mesuree a chaque scan
// et n'atteignait que le classifieur de goulot.
//
// Les trois lecons de la nuit sont appliquees D'ENTREE : le compte vient de sa serie (pas des
// trames), la phrase dit « au moins » (un p95 est un plancher), et aucune des trois causes n'est
// designee — un cap volontaire est un choix, pas un defaut.
// -----------------------------------------------------------------------
public sealed class ScanShowsPresentStallTests
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

    /// <param name="attenteMs">null = PresentMon n'a pas rendu la grandeur.</param>
    /// <param name="echantillons">le compte de sa serie ; null = compte inconnu.</param>
    /// <param name="trames">le nombre de trames, volontairement dissocie du compte.</param>
    private static ScanViewModel Build(double? attenteMs, int? echantillons = 200, int trames = 200)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var bl = new BenchmarkResult(200, 150, 120,
            Enumerable.Repeat(5.0, trames).ToList(), null, 1.0, 5,
            PresentStallMsP95: attenteMs, PresentStallSamples: echantillons);

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(bl));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St, new List<Finding>(), bl)));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        var store = Substitute.For<IDesiredStateStore>();
        store.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Ok());
        store.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));

        return new ScanViewModel(
            hd, sr, br, sc, sd,
            ScanViewModelTestSeams.Safety(), ScanViewModelTestSeams.Engine(),
            ScanViewModelTestSeams.ProfileConsumer(), store, new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key);
    }

    [Fact]
    public async Task Le_scan_DIT_le_temps_passe_a_attendre()
    {
        var vm = Build(attenteMs: 4.2);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.PresentStallLabel.Should().Be("Scan.PresentStall.Measured");
    }

    [Fact]
    public async Task Une_grandeur_NON_RENDUE_par_la_sonde_ne_produit_aucune_ligne()
    {
        // Mesure du 10/09 : sur le compositeur de bureau, `presentStall_p95` sort « non lu ». La
        // grandeur n'est pas toujours publiee, et « non lu » n'est pas zero — un zero se lirait
        // comme « rien ne retient tes images ».
        var vm = Build(attenteMs: null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.PresentStallLabel.Should().BeNull();
    }

    [Fact]
    public async Task Le_compte_vient_de_SA_serie_et_pas_des_trames()
    {
        // La faute payee deux fois dans cette boucle, evitee d'entree ici : mille trames ne
        // disent rien du nombre d'echantillons de `InPresentApi`, qui est une serie a part.
        var vm = Build(attenteMs: 4.2, echantillons: 8, trames: 1000);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.PresentStallLabel.Should().BeNull("un p95 tire de huit points ne devient pas "
                                             + "interpretable parce que la capture a mille trames");
    }

    [Fact]
    public async Task Un_compte_INCONNU_se_tait()
    {
        var vm = Build(attenteMs: 4.2, echantillons: null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.PresentStallLabel.Should().BeNull("« inconnu » n'est pas « assez »");
    }

    [Fact]
    public async Task Au_seuil_exact_la_ligne_existe()
    {
        // Le contre-controle : une garde qui refuse tout ne vaut pas mieux qu'une garde absente.
        var vm = Build(attenteMs: 4.2, echantillons: 20);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.PresentStallLabel.Should().NotBeNull();
    }
}
