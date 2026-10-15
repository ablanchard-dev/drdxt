using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// L'ecran apres scan : inventaire des composants (SSD compris), accroche du verdict, resume des
/// problemes et ligne memoire. Les assertions passent par le traducteur identite (cle en entree = cle
/// en sortie) : elles verifient la logique et le cablage des cles, pas le texte traduit.
/// </summary>
public class ScanDashboardTests
{
    private static HardwareInventory Hw(
        ChassisType c = ChassisType.Desktop,
        MemoryModule[]? memory = null) => new(
        new CpuInfo("AMD Ryzen 7 7800X3D", "AMD", 8, 16, true, 1),
        new[] { new GpuInfo("NVIDIA RTX 4070", "NVIDIA", 12L << 30, "551", TriState.On) },
        memory ?? new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        c);

    private static SettingsState St => new(TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);
    private static BenchmarkResult Bl => new(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

    private static ScanViewModel Build(
        IReadOnlyList<StorageDevice>? storage = null,
        IReadOnlyList<Finding>? findings = null,
        bool benchDegraded = false,
        HardwareInventory? hw = null,
        IBiosAdvisor? biosAdvisor = null,
        Func<string, string>? localiser = null)
    {
        var inventory = hw ?? Hw();

        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(inventory));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(benchDegraded
              ? Result<BenchmarkResult>.Fail(ErrorCodes.BenchmarkFailed, "no game running")
              : Result<BenchmarkResult>.Ok(Bl));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              inventory, St,
              findings ?? new List<Finding>(), Bl,
              // Une machine entièrement lue : ce que le vrai noteur rapporte quand chaque levier applicable
              // était lisible. Ces tests figent la bande du score, donc la couverture ne doit pas être la
              // variable ; le titre de couverture partielle est figé à part dans ScanViewModelDisplayTests.
              Ledger: null,
              Coverage: new ScanCoverage(ApplicableLevers: 6, KnownLevers: 6, UnknownLevers: 0))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            storage ?? new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, localiser ?? (key => key), biosAdvisor);
    }

    [Fact]
    public async Task Components_include_cpu_gpu_ram_ssd_windows_with_real_values()
    {
        var vm = Build();
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Components.Select(c => c.IconKey)
          .Should().ContainInOrder("Cpu", "Gpu", "Ram", "Ssd", "Windows");

        vm.Components.Single(c => c.IconKey == "Cpu").Value.Should().Be("AMD Ryzen 7 7800X3D");
        vm.Components.Single(c => c.IconKey == "Gpu").Value.Should().Be("NVIDIA RTX 4070");
        vm.Components.Single(c => c.IconKey == "Ssd").Value.Should().Contain("Samsung 990 Pro").And.Contain("NVMe");
        vm.Components.Single(c => c.IconKey == "Windows").Value.Should().StartWith("Windows");
    }

    [Fact]
    public async Task Storage_unavailable_shows_dash_not_crash()
    {
        var sdFail = Substitute.For<IStorageDetector>();
        sdFail.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Fail(ErrorCodes.StorageFail, "x"));

        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(Bl));
        var scr = Substitute.For<IScanScorer>();
        scr.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), Bl)));

        var vm = ScanViewModelTestSeams.Build(hd, sr, br, scr, sdFail, k => k);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Components.Single(c => c.IconKey == "Ssd").Value.Should().Be("—");
    }

    [Fact]
    public async Task Storage_with_unknown_media_kind_shows_model_without_suffix()
    {
        // Finition honnête : un type de média vide ou « Unknown » ne doit pas imprimer un vilain « · Unknown ».
        var vm = Build(storage: new List<StorageDevice> { new("Samsung SSD 990 PRO 2TB", "Unknown", 2L << 40) });
        await vm.ScanCommand.ExecuteAsync(null);

        var ssd = vm.Components.Single(c => c.IconKey == "Ssd").Value;
        ssd.Should().Be("Samsung SSD 990 PRO 2TB");
        ssd.Should().NotContain("Unknown");
    }

    [Fact]
    public async Task No_major_finding_gives_no_critical_problem_summary()
    {
        var vm = Build(findings: new List<Finding>
        {
            new("Gpu", Severity.Info, "Finding.Gpu.ReBarUnknown", null),
        });
        await vm.ScanCommand.ExecuteAsync(null);

        vm.ProblemSummary.Should().Be("UI.Result.NoCritical");
    }

    [Fact]
    public async Task Un_constat_mineur_que_la_carte_dit_a_corriger_n_est_pas_resume_en_rien_de_critique()
    {
        // La carte marque deux domaines « À corriger » (VRR inactif, 10 arrêts brutaux en 90 jours) :
        // le résumé ne doit pas dire « Aucun problème critique détecté » en ne comptant que le Majeur.
        // Il compte comme la carte, tout ce qui dépasse Info.
        var vm = Build(findings: new List<Finding>
        {
            new("Latency", Severity.Minor, "Finding.Latency.VrrOff", null),
            new("Gpu", Severity.Info, "Finding.Gpu.ReBarUnknown", null),
        });
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Domaines.Where(d => d.Etat == EtatDomaine.ACorriger).Should().ContainSingle();
        vm.ProblemSummary.Should().StartWith("1 ").And.Contain("UI.Result.ACorrigerSansCritique");
    }

    [Fact]
    public async Task Un_constat_bios_majeur_n_est_pas_resume_en_aucun_critique()
    {
        // XMP coupé : le BIOS porte un constat Majeur (le plus gros gain réel) ; le résumé ne doit pas dire
        // « …, aucun critique » en ne comptant le Majeur que dans les constats de configuration.
        var xmpCoupe = new BiosFinding(FirmwareSettingKind.XmpExpo, Severity.Major, "Bios.Xmp.Off.Title",
            "Bios.Xmp.Off.Rationale", BiosMatrixClass.Guide, TriState.Off, BiosRecommendedAction.Enable,
            "Bios.Xmp.Action.Enable", Gain: null, StateNotReadable: false);
        var vm = Build(biosAdvisor: ScanViewModelTestSeams.BiosAdvisor(xmpCoupe));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.ProblemSummary.Should().NotContain("UI.Result.ACorrigerSansCritique")
            .And.NotBe("UI.Result.NoCritical");
    }

    [Fact]
    public async Task Avec_un_critique_le_resume_compte_encore_tout_ce_que_la_carte_marque()
    {
        // Même scan : le Majeur compté, le résumé ne doit pas dire « 1 optimisation(s) recommandée(s) »
        // et oublier les six autres points que la carte marque « À corriger ».
        var xmpCoupe = new BiosFinding(FirmwareSettingKind.XmpExpo, Severity.Major, "Bios.Xmp.Off.Title",
            "Bios.Xmp.Off.Rationale", BiosMatrixClass.Guide, TriState.Off, BiosRecommendedAction.Enable,
            "Bios.Xmp.Action.Enable", Gain: null, StateNotReadable: false);
        var vm = Build(biosAdvisor: ScanViewModelTestSeams.BiosAdvisor(xmpCoupe),
            findings: new List<Finding> { new("Latency", Severity.Minor, "Finding.Latency.VrrOff", null) });
        await vm.ScanCommand.ExecuteAsync(null);

        vm.ProblemSummary.Should().StartWith("2 ").And.Contain("UI.Result.ACorrigerDontCritiques");
    }

    // Critique impeccable de l'écran de résultat : « 6 point(s) à corriger, dont
    // 1 critique(s) » dans la première phrase que le joueur lit. Des parenthèses de pluriel, c'est
    // l'interface qui n'a pas pris la peine de compter : le contraire de ce que le produit promet.
    private static string Fr(string cle) =>
        new DrDxt.Infrastructure.Localization.ResxLocalizer().Get(cle, new System.Globalization.CultureInfo("fr"));

    private static readonly BiosFinding XmpCoupe = new(FirmwareSettingKind.XmpExpo, Severity.Major, "Bios.Xmp.Off.Title",
        "Bios.Xmp.Off.Rationale", BiosMatrixClass.Guide, TriState.Off, BiosRecommendedAction.Enable,
        "Bios.Xmp.Action.Enable", Gain: null, StateNotReadable: false);

    public static TheoryData<int, bool, string> Phrases => new()
    {
        // Sans « aucun critique », qui rassurait à côté d'incidents de stabilité.
        { 1, false, "1 point à corriger" },
        { 3, false, "3 points à corriger" },
        { 0, true, "1 point à corriger, et il est critique" },
        { 1, true, "2 points à corriger, dont 1 critique" },
    };

    [Theory]
    [MemberData(nameof(Phrases))]
    public async Task Le_resume_accorde_ses_nombres_au_lieu_de_mettre_des_parentheses(int mineurs, bool xmpCoupe, string attendu)
    {
        var vm = Build(
            findings: Enumerable.Range(0, mineurs).Select(i => new Finding("Latency", Severity.Minor, $"f{i}", null)).ToList(),
            biosAdvisor: xmpCoupe ? ScanViewModelTestSeams.BiosAdvisor(XmpCoupe) : null,
            localiser: Fr);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.ProblemSummary.Should().Be(attendu);
    }

    [Fact]
    public async Task Major_findings_count_into_recommendation_summary()
    {
        var vm = Build(findings: new List<Finding>
        {
            new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12),
            new("Latency", Severity.Major, "Finding.Latency.HagsOff", 8),
        });
        await vm.ScanCommand.ExecuteAsync(null);

        // Le localisateur identité rend la clé ; « {0} » n'y est pas, donc le VM préfixe le compte.
        vm.ProblemSummary.Should().StartWith("2 ");
    }

    [Fact]
    public async Task Features_are_four_localized_cards()
    {
        var vm = Build();
        await vm.ScanCommand.ExecuteAsync(null);
        vm.Features.Should().HaveCount(4);
        vm.Features.Select(f => f.IconKey).Should().ContainInOrder("Diagnostic", "Gaming", "Security", "Report");
    }

    // La ligne RAM dit la vitesse ENTRAINEE. Quand le kit est note plus haut, on le mentionne
    // — meme idiome que « (kit CL18) » deja utilise : dire d'ou sort le chiffre. Ce n'est pas
    // un defaut (frequent en dual rank) et ce n'est donc pas un BiosFinding : le canal des
    // findings est celui des problemes a corriger, pas celui des informations.
    [Fact]
    public async Task Ram_line_names_the_kit_rated_speed_when_it_runs_below_it()
    {
        var vm = Build(hw: Hw(memory: new[] { new MemoryModule("F4-4000C18-32GTES", "G.Skill", 3800, 4000) }));
        await vm.ScanCommand.ExecuteAsync(null);

        var ram = vm.Components.Single(c => c.IconKey == "Ram").Value;
        ram.Should().Contain("3800 MT/s", "la vitesse entrainee reste le chiffre principal");
        ram.Should().Contain("(kit 4000)", "le nominal vient du part number et n'etait jamais dit ; meme idiome que (kit CL18)");
        // La latence du kit est nommée (CAS) pour ne pas se confondre avec le temps
        // d'accès mesuré, et aucun « · » ne peut finir une ligne (espace insécable devant).
        ram.Should().Contain(" · CAS ").And.NotContain(" · ");
    }

    [Fact]
    public async Task Ram_line_says_nothing_extra_when_the_kit_runs_at_its_rated_speed()
    {
        var vm = Build(hw: Hw(memory: new[] { new MemoryModule("F4-4000C18-32GTES", "G.Skill", 4000, 4000) }));
        await vm.ScanCommand.ExecuteAsync(null);

        var ram = vm.Components.Single(c => c.IconKey == "Ram").Value;
        ram.Should().Contain("4000 MT/s");
        ram.Should().NotContain("(kit 4000)", "au nominal il n'y a AUCUNE marge a nommer — ne rien fabriquer");
    }

    [Fact]
    public async Task Ram_line_names_no_rated_speed_when_it_was_never_read()
    {
        // RatedMhz = 0 => jamais lu. On ne compare pas contre un zero et on n'invente rien.
        var vm = Build(hw: Hw(memory: new[] { new MemoryModule("PN", "G.Skill", 3800, 0) }));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Components.Single(c => c.IconKey == "Ram").Value.Should().Contain("3800 MT/s");
    }

}
