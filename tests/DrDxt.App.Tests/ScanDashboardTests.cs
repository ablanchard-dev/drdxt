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
    public async Task La_carte_graphique_affichee_est_la_carte_dediee_meme_listee_apres_la_puce_integree()
    {
        var uhd = new GpuInfo("Intel(R) UHD Graphics 770", "Intel", 128L << 20, "31.0", TriState.Unknown);
        var vm = Build(hw: Hw() with { Gpus = new[] { uhd, Hw().Gpus[0] } });
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Components.Single(c => c.IconKey == "Gpu").Value.Should().Be("NVIDIA RTX 4070");
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

    // L'accueil ouvrait sur « 12 points à corriger », l'inventaire de ce qui ne va pas, avant Optimiser. Son titre dit l'état
    // de l'analyse ; le nombre vit dans le dépliant des points, le plus grave en tête. Le conseil sous le titre (« joue une
    // partie, puis relance ») se tait ; il ne parle que d'une analyse en échec ou partielle.
    [Fact]
    public async Task Le_titre_de_l_accueil_dit_l_etat_et_garde_le_nombre_pour_le_depliant()
    {
        var vm = Build(findings: new List<Finding> { new("Latency", Severity.Minor, "Finding.Latency.VrrOff", null) });
        await vm.ScanCommand.ExecuteAsync(null);

        vm.TitreAccueil.Should().Be("Accueil.Titre");
        vm.LabelAFaire.Should().NotBeNullOrEmpty("le nombre reste, dans le dépliant");
        vm.SousTitreAccueil.Should().BeEmpty("un conseil n'est pas un état");
    }

    // Une analyse partielle (perf en jeu pas encore mesurée, le cas d'un premier scan) est une analyse qui a abouti : même
    // titre, même silence. Seul l'échec garde son résumé et dit ce qui manque.
    [Theory]
    [InlineData(ScanState.Degraded, true)]
    [InlineData(ScanState.Failed, false)]
    public void Une_analyse_partielle_garde_le_titre_neutre_un_echec_dit_ce_qui_manque(ScanState etat, bool neutre)
    {
        var vm = Build(findings: new List<Finding>());
        vm.SubtitleText = "ce qui manque";
        vm.State = etat;

        (vm.TitreAccueil == "Accueil.Titre").Should().Be(neutre);
        (vm.SousTitreAccueil.Length == 0).Should().Be(neutre);
    }

    [Fact]
    public async Task Un_point_critique_ne_change_pas_le_titre_et_passe_en_tete_du_depliant()
    {
        var xmpCoupe = new BiosFinding(FirmwareSettingKind.XmpExpo, Severity.Major, "Bios.Xmp.Off.Title",
            "Bios.Xmp.Off.Rationale", BiosMatrixClass.Guide, TriState.Off, BiosRecommendedAction.Enable,
            "Bios.Xmp.Action.Enable", Gain: null, StateNotReadable: false);
        var vm = Build(biosAdvisor: ScanViewModelTestSeams.BiosAdvisor(xmpCoupe));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.TitreAccueil.Should().Be("Accueil.Titre");
        vm.PointsAFaire.First().Entree.Gravite.Should().Be(Severity.Major, "le critique ouvre la liste");
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

    // La mesure dit que ce levier ne rendra pas d'images ici : le constat reste sur la carte et dit pourquoi.
    [Fact]
    public async Task Une_promesse_retiree_par_la_mesure_se_lit_sur_la_carte()
    {
        const string motif = "Ta carte graphique est le goulot. Ça ne te donnera pas d'images sur cette machine.";
        var vm = Build(findings: new List<Finding>
        {
            new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12, GainSuppressedReason: motif),
            new("Latency", Severity.Minor, "Finding.Latency.VrrOff", null),
        });
        await vm.ScanCommand.ExecuteAsync(null);

        var lignes = vm.Domaines.SelectMany(d => d.Lignes).ToList();
        lignes.Single(l => l.Cle == "Finding.Memory.XmpOff").Detail.Should().Contain(motif);
        lignes.Single(l => l.Cle == "Finding.Latency.VrrOff").Detail.Should().NotContain(motif);
    }

    // XMP coupé vit aussi dans le guide BIOS : le motif de la mesure y suit la raison, pas seulement sur le constat du scan.
    [Fact]
    public async Task Le_guide_bios_dit_aussi_la_promesse_retiree_pour_xmp()
    {
        const string motif = "Ta carte graphique est le goulot. Ça ne te donnera pas d'images sur cette machine.";
        var vm = Build(biosAdvisor: ScanViewModelTestSeams.BiosAdvisor(XmpCoupe),
            findings: new List<Finding> { new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12, GainSuppressedReason: motif) });
        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosFindings.Single(b => b.Kind == nameof(FirmwareSettingKind.XmpExpo)).Rationale.Should().EndWith(motif);
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
