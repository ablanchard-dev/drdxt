using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// La saccade, jusqu'a l'ecran — et le renversement qui l'a fait remarquer.
//
// L'ecran montre le 1 % LOW, dont ce depot a mesure qu'il resout mal : ±132 % en session live,
// jusqu'a 43,7 % d'ecart sous no-op sur la partie la plus agitee des huit captures Warzone. Et il
// cachait le p95 des frametimes — meme famille, la queue lente, mais assis sur 5 % des trames au
// lieu de 1 %, donc pas dicte par une poignee d'a-coups. Mesure a chaque scan, lu par personne
// dans `DrDxt.App`.
//
// Les deux restent necessaires : sur un a-coup toutes les 60 trames, le p95 rend +1,6 % quand
// le 1 % low prouve -65,6 %. Cette ligne AJOUTE, elle ne remplace pas — et le test qui suit verifie
// que le 1 % low est toujours la.
// -----------------------------------------------------------------------
public sealed class ScanShowsStutterTests
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

    /// <param name="p95Ms">null = la grandeur n'a pas ete calculee.</param>
    /// <param name="trames">trames VALIDES de la capture.</param>
    /// <param name="trainees">trames a zero ajoutees a la serie : elles ne portent aucun
    /// percentile et ne doivent pas gonfler le compte.</param>
    private static ScanViewModel Build(double? p95Ms, int trames = 200, int trainees = 0)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var series = Enumerable.Repeat(5.0, trames).Concat(Enumerable.Repeat(0.0, trainees)).ToList();
        var bl = new BenchmarkResult(200, 150, 120, series, null, 1.0, 5, P95FrametimeMs: p95Ms);

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
    public async Task Le_scan_DIT_les_trames_les_plus_lentes()
    {
        var vm = Build(p95Ms: 12.4);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.StutterLabel.Should().Be("Scan.Stutter.Measured");
    }

    [Fact]
    public async Task Elle_AJOUTE_au_1_pour_cent_low_au_lieu_de_le_remplacer()
    {
        // Les deux grandeurs sont necessaires : sur un a-coup toutes les 60 trames, le p95 rend
        // +1,6 % quand le 1 % low prouve -65,6 %. Retirer l'un pour l'autre serait une perte.
        var vm = Build(p95Ms: 12.4);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.StutterLabel.Should().NotBeNull();
        vm.P1LowDisplay.Should().Be("150", "le 1 % low reste affiche");
    }

    [Fact]
    public async Task Sans_p95_calcule_la_ligne_n_existe_pas()
    {
        var vm = Build(p95Ms: null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.StutterLabel.Should().BeNull();
    }

    [Fact]
    public async Task Le_compte_porte_sur_les_trames_EXPLOITABLES_et_pas_sur_la_longueur()
    {
        // La faute payee deux fois dans cette boucle : juger un percentile sur une population
        // qui n'est pas la sienne. Mille trames a zero ne portent aucun p95 — le filtre du
        // percentile les ecarte, et le compte doit les ecarter aussi.
        var vm = Build(p95Ms: 12.4, trames: 8, trainees: 1000);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.StutterLabel.Should().BeNull("mille zeros ne rendent pas interpretable un p95 tire de huit");
    }

    [Fact]
    public async Task Au_seuil_exact_la_ligne_existe()
    {
        // Le contre-controle : une garde qui refuse tout ne vaut pas mieux qu'une garde absente.
        var vm = Build(p95Ms: 12.4, trames: 20);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.StutterLabel.Should().NotBeNull();
    }

    /// <summary>
    /// Le sens du p95 ne s'inverse pas. « Les 5 % les plus lentes durent jusqu'a 8,8 ms » dirait le
    /// contraire de ce qu'un 95e centile signifie : 5 % des trames sont au-dessus de cette valeur, et
    /// rien ne borne leur duree. Ecrit ainsi, le produit minimiserait la saccade en pretendant la montrer.
    ///
    /// Ce test verrouille la phrase des deux cotes : elle doit dire « au moins », et elle ne doit
    /// plus jamais dire « jusqu'a ».
    /// </summary>
    ///
    /// Et le verrou couvre les deux phrases de percentile : la cadence peut porter la meme inversion
    /// que la saccade (« derive jusqu'a X ms au 95e centile »).
    [Theory]
    [InlineData("fr", "Scan.Stutter.Measured")]
    [InlineData("en", "Scan.Stutter.Measured")]
    [InlineData("fr", "Scan.Cadence.Measured")]
    [InlineData("en", "Scan.Cadence.Measured")]
    [InlineData("fr", "Scan.PresentStall.Measured")]
    [InlineData("en", "Scan.PresentStall.Measured")]
    public void La_phrase_dit_AU_MOINS_et_jamais_JUSQU_A(string culture, string cle)
    {
        var texte = new DrDxt.Infrastructure.Localization.ResxLocalizer()
            .Get(cle, new System.Globalization.CultureInfo(culture));

        texte.Should().ContainEquivalentOf(culture == "fr" ? "au moins" : "at least",
            "un p95 est un PLANCHER pour les 5 % concernees, pas un plafond");
        texte.Should().NotContainEquivalentOf(culture == "fr" ? "jusqu'à" : "up to",
            "cette formulation minimise la saccade en pretendant la montrer");
    }
}
