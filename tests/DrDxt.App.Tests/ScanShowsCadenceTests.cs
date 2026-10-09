using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// La cadence, jusqu'a l'ecran.
//
// Le depot ecrit lui-meme que l'erreur d'animation est la seule grandeur qui sache dire qu'une
// moyenne de 140 FPS est desagreable a jouer. Elle est mesuree a chaque scan, sur la vraie partie
// du joueur, et elle n'atteignait que le classifieur de goulot : le chiffre ne sortait nulle part.
//
// Ces tests portent autant sur les deux SILENCES que sur la phrase — une grandeur non mesuree et
// un p95 qui ne merite pas son nom se taisent tous les deux.
// -----------------------------------------------------------------------
public sealed class ScanShowsCadenceTests
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

    /// <param name="ecartMs">null = PresentMon n'a pas rendu la grandeur.</param>
    /// <param name="trames">le nombre de TRAMES de la capture.</param>
    /// <param name="echantillons">le nombre d'echantillons d'ANIMATION derriere le p95. Par
    /// defaut il suit les trames ; les tests qui comptent le dissocient exprès.</param>
    /// <param name="compteInconnu">true = mesure enregistree avant que le compte existe.</param>
    private static ScanViewModel Build(double? ecartMs, int trames = 200, int? echantillons = null,
        bool compteInconnu = false)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var bl = new BenchmarkResult(200, 150, 120,
            Enumerable.Repeat(5.0, trames).ToList(), null, 1.0, 5,
            AnimationErrorMsP95: ecartMs,
            AnimationErrorSamples: compteInconnu ? null : echantillons ?? trames);

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(bl));

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
    public async Task Le_scan_DIT_la_cadence_mesuree()
    {
        var vm = Build(ecartMs: 3.4);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.CadenceLabel.Should().Be("Scan.Cadence.Measured",
            "la localisation factice rend la cle ; le texte est verrouille par ScanLocalizationTests");
    }

    [Fact]
    public async Task Une_grandeur_NON_MESUREE_ne_produit_aucune_ligne()
    {
        // PresentMon ne rend pas toujours l'erreur d'animation. « Non lu » n'est pas zero, et un
        // zero se lirait comme une cadence parfaite.
        var vm = Build(ecartMs: null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.CadenceLabel.Should().BeNull();
    }

    [Fact]
    public async Task Un_p95_calcule_sur_TROP_PEU_de_trames_se_tait()
    {
        // Le cas paye en vrai : huit trames de terminal ont rendu « erreur d'animation p95 =
        // 1 186,95 ms ». Un chiffre juste, plausible, et vide de sens — a cette cadence il recopie
        // l'intervalle entre deux images. Sous vingt echantillons, un p95 se confond avec le
        // maximum : ce n'est pas une prudence, c'est de l'arithmetique.
        var vm = Build(ecartMs: 1186.95, trames: 8);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.CadenceLabel.Should().BeNull("mieux vaut se taire qu'annoncer une saccade d'une seconde");
    }

    [Fact]
    public async Task La_garde_compte_les_echantillons_d_ANIMATION_et_pas_les_TRAMES()
    {
        // Le defaut que ce test fige. Le p95 de l'erreur d'animation repose sur une serie a
        // Part : une passe dont cette serie manque ou n'est pas appariee ne contribue pas au
        // percentile, et le nombre de trames n'en sait rien. Compter les trames validait donc un
        // p95 tire de huit points en en lisant mille — la meme faute que deux moyennes prises sur
        // des trames differentes.
        var vm = Build(ecartMs: 1186.95, trames: 1000, echantillons: 8);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.CadenceLabel.Should().BeNull("mille trames ne rendent pas interpretable un p95 tire de huit");
    }

    [Fact]
    public async Task Un_compte_INCONNU_se_tait()
    {
        // Mesure enregistree avant que le compte existe : « inconnu » n'est pas « assez ».
        var vm = Build(ecartMs: 3.4, trames: 1000, compteInconnu: true);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.CadenceLabel.Should().BeNull();
    }

    [Fact]
    public async Task Au_seuil_exact_la_ligne_existe()
    {
        // Le contre-controle : une garde qui refuserait tout serait aussi inutile qu'une garde
        // absente. Vingt echantillons est le premier compte ou 1/20 = 5 % ecarte un point du max.
        var vm = Build(ecartMs: 3.4, trames: 20);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.CadenceLabel.Should().NotBeNull();
    }
}
