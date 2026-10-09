using System.Collections.Generic;
using System.Threading;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

public class ScanViewModelTests
{
    private static HardwareInventory Hw(ChassisType c = ChassisType.Desktop) => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "AMD", 16L << 30, "1", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        c);

    private static SettingsState St => new(TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);
    private static BenchmarkResult Bl => new(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

    private static IStorageDetector Storage()
    {
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));
        return sd;
    }

    private static (IHardwareDetector, ISettingsStateReader, IBenchmarkRunner, IScanScorer) Happy(int score = 68)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(Bl));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding> { new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12) }, score, Bl,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));
        return (hd, sr, br, sc);
    }

    private static ScanViewModel Build((IHardwareDetector, ISettingsStateReader, IBenchmarkRunner, IScanScorer) m)
        => ScanViewModelTestSeams.Build(m.Item1, m.Item2, m.Item3, m.Item4, Storage(), key => key);

    [Fact]
    public async Task Happy_path_sets_done_with_score_and_findings()
    {
        var vm = Build(Happy(68));
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Done);
        vm.Score.Should().Be(68);
        vm.Findings.Should().NotBeEmpty();
    }

    // ── « Deja bien reglee » (revue du 12/09, M1) ──
    // Cet etat se decidait sur le score /100 (>= 90), retire de tout ce que le client lit le 05/09.
    // Il se decide maintenant sur les faits de lecture : rien a corriger et tout a ete lu.

    private static RepairFinding Constat(string nom, RepairStatus statut) =>
        new("POWER", nom, statut == RepairStatus.Unknown ? null : "lue", null, statut, "parce que.", RepairAction.Aucune);

    private static ScanViewModel SansConstatDuScanner(Func<IReadOnlyList<RepairFinding>>? audit, int score = 40,
        ScanCoverage? couverture = null, DrDxt.App.Profiles.ScanProfileConsumption? consommation = null)
    {
        var (hd, sr, br, _) = Happy();
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), score, Bl,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false),
              Coverage: couverture ?? new ScanCoverage(ApplicableLevers: 6, KnownLevers: 6, UnknownLevers: 0))));
        return ScanViewModelTestSeams.Build(hd, sr, br, sc, Storage(), k => k, repairAudit: audit,
            profileConsumer: ScanViewModelTestSeams.ProfileConsumer(consommation));
    }

    private static readonly Func<IReadOnlyList<RepairFinding>> Sain =
        () => new[] { Constat("sain", RepairStatus.ExpectedDefault) };

    [Fact]
    public async Task Avec_le_VRAI_scorer_une_machine_reglee_et_toute_lue_peut_etre_deja_reglee()
    {
        // 14/09/2026 (relecture 12/20) : le vrai ScanScorer ajoute toujours
        // « Finding.Latency.Available » (Info, gain 0). DejaReglee comptait tous les constats : avec le
        // vrai scorer, « deja bien reglee » etait inatteignable, et les tests de cette section ne le
        // voyaient pas parce qu'ils SUBSTITUENT le scorer. Celui-ci ne substitue rien.
        // Une machine vraiment reglee et toute lue : pilote recent (AMD 24.x, au-dessus du plancher 20),
        // deux barrettes sur deux canaux, MMCSS intact, pagefile present, ecran deja a sa frequence max.
        var hwReglee = new HardwareInventory(
            new CpuInfo("CPU", "AMD", 8, 16, false, 1),
            new[] { new GpuInfo("GPU", "AMD", 16L << 30, "24.1", TriState.On) },
            new[]
            {
                new MemoryModule("PN", "Corsair", 6000, 6000, DeviceLocator: "Controller0-DIMMA2"),
                new MemoryModule("PN", "Corsair", 6000, 6000, DeviceLocator: "Controller1-DIMMB2"),
            },
            new MotherboardInfo("MSI", "B650"),
            new BiosInfo("1", DateTimeOffset.UtcNow, true),
            ChassisType.Desktop);
        var reglee = St with
        {
            VrrCapableDisplay = TriState.Off,
            PagefileConfigured = TriState.On,
            MmcssIntact = TriState.On,
            DisplayRefresh = new RefreshHeadroom(TriState.Off, 144, 144),
        };
        var (_, _, br, _) = Happy();
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(hwReglee));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(reglee));
        var scorer = new DrDxt.Detection.Scoring.ScanScorer();
        var vu = scorer.Score(hwReglee, reglee, Bl).Value!;

        var vm = ScanViewModelTestSeams.Build(hd, sr, br, scorer, Storage(), k => k, repairAudit: Sain);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.State.Should().Be(ScanState.AlreadyOptimal,
            $"couverture {vu.Coverage} ; constats : {string.Join(" | ", vu.Findings)}");
    }

    [Fact]
    public async Task Un_reglage_encore_au_plan_n_est_jamais_deja_regle()
    {
        // Revue du 14/09 : cette clause de DejaReglee etait NOMMEE, jamais prouvee.
        var vm = SansConstatDuScanner(Sain,
            consommation: ScanViewModelTestSeams.PlanConsumption(new[] { "fixit.power_plan" }));
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task Des_leviers_GPU_fermes_par_decision_n_empechent_pas_deja_regle()
    {
        // 14/09/2026 : DejaReglee exigeait HasGpuPlan == false. Or sur toute machine NVIDIA de bureau
        // les leviers GPU s'appliquent, et la decision produit interdit d'y ecrire : « deja bien reglee »
        // n'etait atteignable sur aucune d'elles, pour des leviers que le produit ne proposera pas.
        var vm = SansConstatDuScanner(Sain,
            consommation: ScanViewModelTestSeams.GpuOnlyConsumption("gpu.undervolt"));
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.AlreadyOptimal);
    }

    [Fact]
    public async Task Un_levier_que_le_scanner_n_a_pas_pu_lire_n_est_jamais_deja_regle()
    {
        // 14/09/2026 (C1 bis) : le scanner rend Unknown sans constat (XMP, rafraichissement,
        // pagefile, MMCSS). « Aucun constat » ne dit pas « tout lu » : la couverture, si.
        var vm = SansConstatDuScanner(() => new[] { Constat("sain", RepairStatus.ExpectedDefault) },
            couverture: new ScanCoverage(ApplicableLevers: 6, KnownLevers: 5, UnknownLevers: 1));
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task Un_score_de_94_ne_dit_plus_deja_reglee_quand_un_constat_reste()
    {
        var vm = Build(Happy(94));
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task Deja_reglee_quand_rien_n_est_a_corriger_et_que_tout_a_ete_lu_quel_que_soit_le_score()
    {
        var vm = SansConstatDuScanner(() => new[] { Constat("sain", RepairStatus.ExpectedDefault) }, score: 40);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.AlreadyOptimal);
    }

    [Fact]
    public async Task Un_etat_non_lu_n_est_jamais_deja_regle()
    {
        var vm = SansConstatDuScanner(() => new[]
        {
            Constat("sain", RepairStatus.ExpectedDefault),
            Constat("illisible", RepairStatus.Unknown),
        }, score: 99);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task Un_conseiller_BIOS_en_panne_n_a_rien_lu_donc_jamais_deja_regle()
    {
        var (hd, sr, br, _) = Happy();
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), 99, Bl,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, Storage(), k => k,
            biosAdvisor: ScanViewModelTestSeams.BiosAdvisorFailing(),
            repairAudit: () => new[] { Constat("sain", RepairStatus.ExpectedDefault) });
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task Sans_audit_de_sante_rien_n_est_affirme_deja_regle()
    {
        var vm = SansConstatDuScanner(audit: null, score: 99);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Done, "un audit qui n'a pas tourne n'a rien lu");
    }

    [Fact]
    public async Task Benchmark_failure_degrades_to_detection_only()
    {
        var (hd, sr, _, sc) = Happy();
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable, "x"));
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding> { new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12) }, 68, null,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, Storage(), k => k);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Degraded);
        vm.Findings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Detection_failure_sets_failed_state()
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Fail(ErrorCodes.WmiFail, "x"));
        var (_, sr, br, sc) = Happy();
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, Storage(), k => k);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Failed);
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Settings_failure_sets_failed_state()
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Fail(ErrorCodes.PowerFail, "x"));
        var (_, _, br, sc) = Happy();
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, Storage(), k => k);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Failed);
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Scorer_failure_sets_failed_state_with_analysis_error_key()
    {
        var (hd, sr, br, _) = Happy();
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Fail(ErrorCodes.NoBaseline, "x"));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, Storage(), k => k);   // localize is identity in tests
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Failed);
        vm.ErrorMessage.Should().Be("Scan.Error.Analyse"); // not the detection key (review must-fix)
    }

    [Fact]
    public async Task State_is_idle_before_run_then_done()
    {
        var vm = Build(Happy(68));
        vm.State.Should().Be(ScanState.Idle);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task Handheld_chassis_surfaces_gating_note()
    {
        var (_, sr, br, sc) = Happy();
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw(ChassisType.Handheld)));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, Storage(), k => k);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.GatingNote.Should().NotBeNullOrEmpty();
    }

    // ── 17/09/2026 : l'écran de résultat se contredisait (défaut D22) ──
    // Vu sur la machine de reference après la refonte : titre « rien à corriger dans ce qui a été lu » et
    // résumé « configuration globalement saine » pendant que l'audit de santé trouvait des états à
    // regarder (Windows : à corriger), et « lecture impossible » alors que tout avait été lu et que
    // rien du catalogue ne s'appliquait.

    [Fact]
    public async Task Un_etat_de_sante_suspect_interdit_le_titre_rien_a_corriger()
    {
        var vm = SansConstatDuScanner(() => new[] { Constat("suspect", RepairStatus.Suspicious) });
        await vm.ScanCommand.ExecuteAsync(null);

        // 25/09/2026 : aucun réglage n'est en cause, le titre ne dit plus « réglages à corriger ».
        vm.VerdictText.Should().Be("UI.Verdict.SanteACorriger").And.NotBe("UI.Verdict.Clean");
    }

    // 29/09/2026, photo du bas de page : « Des points à reprendre. 12 points à corriger », la même idée deux
    // fois, sous le titre qui la disait déjà. Le compte suffit ; le titre reste au-dessus.
    [Fact]
    public async Task Le_resume_du_rapport_ne_redit_pas_le_titre_quand_il_compte_deja_les_points()
    {
        var vm = SansConstatDuScanner(() => new[] { Constat("suspect", RepairStatus.Suspicious) });
        await vm.ScanCommand.ExecuteAsync(null);

        vm.AssistantSummary.Should().NotContain("UI.Verdict").And.Be(vm.ProblemSummary);
    }

    [Fact]
    public async Task Une_machine_saine_garde_son_verdict_dans_le_resume()
    {
        var vm = SansConstatDuScanner(Sain);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.AssistantSummary.Should().Contain("UI.Verdict.Clean", "témoin : sans point compté, le verdict porte la phrase");
    }

    [Fact]
    public async Task Une_machine_saine_et_toute_lue_garde_son_titre_rien_a_corriger()
    {
        var vm = SansConstatDuScanner(Sain);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.VerdictText.Should().Be("UI.Verdict.Clean");
    }

    [Fact]
    public async Task Quand_rien_du_catalogue_ne_s_applique_le_registre_ne_dit_pas_lecture_impossible()
    {
        var vm = SansConstatDuScanner(Sain, couverture: new ScanCoverage(ApplicableLevers: 0, KnownLevers: 0, UnknownLevers: 0));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.RegisterText.Should().Be("UI.Register.NothingApplies");
    }

    [Fact]
    public async Task Des_constats_d_information_seuls_ne_font_pas_quelques_optimisations_possibles()
    {
        var (hd, sr, br, _) = Happy();
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St,
              new List<Finding> { new("Network", Severity.Info, "Finding.Network.LinkPowerSavingOn", null) }, 40, Bl,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false),
              Coverage: new ScanCoverage(ApplicableLevers: 6, KnownLevers: 6, UnknownLevers: 0))));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, Storage(), k => k, repairAudit: Sain);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.ProblemSummary.Should().Be("UI.Result.NoCritical", "un constat d'information n'est pas un point à corriger");
    }

    [Fact]
    public async Task Le_registre_se_redit_quand_la_couverture_arrive()
    {
        // 17/09/2026 : l'écran gardait « lecture impossible » (texte calculé avant la couverture)
        // sous un titre qui, lui, avait été recalculé. Une phrase affichée doit prévenir quand elle change.
        var vm = SansConstatDuScanner(Sain);
        var notifie = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ScanViewModel.RegisterText)) notifie = true; };

        await vm.ScanCommand.ExecuteAsync(null);

        notifie.Should().BeTrue();
        vm.RegisterText.Should().NotBe("UI.Register.NoCoverage");
    }

    // 23/09/2026 — DrDXT est gratuit : aucune ligne de la carte n'est plus « reservee ». Le
    // constat s'affiche entier, avec son titre, sans accroche ni « Debloquer ».
    [Fact]
    public async Task Sur_la_carte_chaque_constat_se_dit_en_entier()
    {
        var (hd, sr, br, _) = Happy();
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St,
              new List<Finding> { new("Network", Severity.Info, "Finding.Network.LinkPowerSavingOn", null) }, 40, Bl,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false),
              Coverage: new ScanCoverage(ApplicableLevers: 6, KnownLevers: 6, UnknownLevers: 0))));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, Storage(), k => k, repairAudit: Sain);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Domaines.Single(d => d.Cle == "reseau").Lignes.Single().Titre
            .Should().Be("Finding.Network.LinkPowerSavingOn");
        typeof(EntreeCarte).GetProperties().Select(p => p.Name)
            .Should().NotContain(new[] { "Reserve", "Accroche" });
    }
}
