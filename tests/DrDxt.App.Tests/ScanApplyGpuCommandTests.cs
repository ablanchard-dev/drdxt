using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Navigation;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// M6 — the opt-in, isolated GPU-tuning apply COMMAND on the ScanViewModel.
//
// ApplyGpuTuningCommand (master §15/§16) is a SEPARATE action from the safe Apply:
//   * disabled until a scan produced results AND the profile recommended an APPLICABLE GPU
//     step (unsupported GPU → no GPU plan → stays disabled — never silently applies an OC);
//   * runs the restore-point gate first, then the dedicated GpuTuningApplyStep (the GPU plan
//     as its own engine batch) — it never runs the safe non-GPU plan;
//   * a clean GPU failure surfaces an explicit localized message and does not navigate;
//   * a successful GPU apply navigates to the dashboard with the real GPU report.
//
// The step is a fake here (its isolation contract is GpuTuningApplyStepTests; the real-engine
// "GPU failure never rolls back the safe batch" is GpuTuningIsolationTests).
//
// Revue du 12/09 (C3) : le bouton passe desormais par le meme jugement que l'Optimiser, avant
// l'achat. Tant que la decision « rien ne s'ecrit sur la carte depuis l'App » tient
// (DiagnosticDeScan : TuningGpuAutorise = false), aucun levier GPU n'est retenu : l'etape n'est
// jamais atteinte, rien n'est vendu. L'ordre achat → restauration → etape redevient observable le
// jour ou cette decision est levee ; ces tests-la le diront en echouant.
// -----------------------------------------------------------------------
public sealed class ScanApplyGpuCommandTests
{
    private const int Score = 68;

    private static readonly GpuTuningTarget DefaultTuning = new(0, -100, 0);
    private static readonly GpuPowerLimitTarget DefaultPower = new(0, 90);

    private static HardwareInventory Hw() => new(
        new CpuInfo("AMD Ryzen 7 7800X3D", "AMD", 8, 16, true, 1),
        new[] { new GpuInfo("NVIDIA RTX 4070", "NVIDIA", 12L << 30, "551", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static BenchmarkResult Bl => new(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

    private static ApplyReport GpuReport(params string[] ids)
    {
        var outcomes = ids
            .Select(id => new ApplyOutcome(id, Applied: true, AutoReverted: false,
                new TweakSnapshot(id, "{}"), Error: null))
            .ToList();
        return new ApplyReport(outcomes, new AbVerdict(Bl, Bl, IsMeasurableGain: false, "verdict.no_measurable_gain"));
    }

    // A consumption with a GPU plan (so the opt-in GPU action is offered). The safe plan can
    // be empty — the GPU action is independent of it.
    private static ScanProfileConsumption WithGpuPlan(bool tested = false) =>
        new(new TweakPlan(Array.Empty<string>()), DefaultTuning, DefaultPower,
            new[] { ScanViewModelTestSeams.Item("gpu.undervolt", tested: tested, inPlan: false) })
        {
            GpuPlan = new TweakPlan(new[] { "gpu.undervolt" }),
        };

    private sealed class Harness
    {
        public required ScanViewModel Vm { get; init; }
        public required ISafetyManager Safety { get; init; }
        public required ITweakEngine Engine { get; init; }
        public required IGpuTuningApplyStep GpuStep { get; init; }
        public required INavigationService Nav { get; init; }
        public required DashboardViewModel Dashboard { get; init; }
    }

    private static Harness Build(
        ScanProfileConsumption? consumption = null,
        Result? restoreResult = null,
        Result<ApplyReport>? gpuResult = null)
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
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St,
              new List<Finding> { new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12) },
              Score, Bl,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        var safety = Substitute.For<ISafetyManager>();
        safety.EnsureRestorePointAsync().Returns(restoreResult ?? Result.Ok());

        // The safe-batch engine must never be called by the GPU action.
        var engine = ScanViewModelTestSeams.Engine();

        var consumer = ScanViewModelTestSeams.ProfileConsumer(consumption ?? WithGpuPlan());

        var gpuStep = ScanViewModelTestSeams.GpuTuningStep(
            gpuResult ?? Result<ApplyReport>.Ok(GpuReport("gpu.undervolt")));

        var nav = Substitute.For<INavigationService>();
        var dashboard = new DashboardViewModel();
        var store = ScanViewModelTestSeams.DesiredStateStore();

        var vm = new ScanViewModel(
            hd, sr, br, sc, sd, safety, engine, consumer, gpuStep, nav, () => dashboard,
            store, new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key);

        return new Harness
        {
            Vm = vm, Safety = safety, Engine = engine, GpuStep = gpuStep, Nav = nav, Dashboard = dashboard,
        };
    }

    // ====================== enablement (opt-in / unsupported GPU) ======================

    [Fact]
    public void Gpu_apply_is_disabled_before_a_scan_has_results()
    {
        var h = Build();
        h.Vm.ApplyGpuTuningCommand.CanExecute(null).Should().BeFalse();
        h.Vm.CanApplyGpuTuning.Should().BeFalse();
    }

    [Fact]
    public async Task Gpu_apply_is_enabled_after_a_scan_that_recommends_an_applicable_gpu_step()
    {
        var h = Build(consumption: WithGpuPlan());
        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.ApplyGpuTuningCommand.CanExecute(null).Should().BeTrue();
        h.Vm.HasGpuTuningRecommendation.Should().BeTrue();
    }

    [Fact]
    public async Task Gpu_apply_stays_disabled_when_no_gpu_step_is_recommended()
    {
        // Unsupported GPU / GPU-only profile dropped → empty GPU plan → the action is a no-op
        // and must stay disabled (never silently apply an OC).
        var h = Build(consumption: ScanViewModelTestSeams.EmptyConsumption());
        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.ApplyGpuTuningCommand.CanExecute(null).Should().BeFalse();
        h.Vm.HasGpuTuningRecommendation.Should().BeFalse();
    }

    [Fact]
    public async Task Unverified_gpu_recommendation_is_surfaced_as_not_verified()
    {
        var h = Build(consumption: WithGpuPlan(tested: false));
        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.GpuTuningVerified.Should().BeFalse(); // "non vérifié"
        h.Vm.LabelUnverified.Should().Be("UI.Profile.Unverified");
    }

    // ====================== restore gate → dedicated step (isolation) ======================

    [Fact]
    public async Task Tant_que_la_decision_tient_le_bouton_GPU_n_atteint_ni_la_carte_ni_la_restauration_ni_le_moteur()
    {
        var h = Build(consumption: WithGpuPlan());
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyGpuTuningCommand.ExecuteAsync(null);

        await h.GpuStep.DidNotReceive().ApplyAsync(
            Arg.Any<ScanProfileConsumption>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await h.Safety.DidNotReceive().EnsureRestorePointAsync();
        // The GPU action never runs the safe non-GPU batch.
        await h.Engine.DidNotReceive().ApplyAsync(
            Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingApplicable");
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.Discarded · tweak.gpu.undervolt.title");
    }

    [Fact]
    public async Task Le_jugement_passe_avant_la_restauration_meme_quand_elle_serait_refusee()
    {
        var h = Build(restoreResult: Result.Fail(ErrorCodes.RestoreDisabled, "off"));
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyGpuTuningCommand.ExecuteAsync(null);

        await h.Safety.DidNotReceive().EnsureRestorePointAsync();
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingApplicable");
        h.Nav.DidNotReceive().NavigateToDashboard(Arg.Any<object>());
    }

    // ====================== outcomes ======================

    [Fact]
    public async Task Un_rapport_GPU_reussi_n_est_jamais_affiche_quand_rien_n_a_ete_retenu()
    {
        var h = Build(gpuResult: Result<ApplyReport>.Ok(GpuReport("gpu.undervolt", "gpu.power_limit")));
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyGpuTuningCommand.ExecuteAsync(null);

        h.Nav.DidNotReceive().NavigateToDashboard(Arg.Any<object>());
        h.Dashboard.AppliedTweakIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Gpu_step_failure_surfaces_message_and_does_not_navigate()
    {
        // Aujourd'hui l'etape n'est pas atteinte (voir l'en-tete) : c'est le refus du jugement
        // qui s'affiche. L'echec de l'etape elle-meme est couvert par GpuTuningApplyStepTests.
        var h = Build(gpuResult: Result<ApplyReport>.Fail(ErrorCodes.BatchReverted, "boom"));
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyGpuTuningCommand.ExecuteAsync(null);

        h.Nav.DidNotReceive().NavigateToDashboard(Arg.Any<object>());
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingApplicable");
    }

    [Fact]
    public async Task Rien_a_tenter_ne_touche_pas_la_carte()
    {
        // C3 : le jugement passe d'abord ; rien de retenu ⇒ rien ne s'ecrit.
        var h = Build(consumption: WithGpuPlan());
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyGpuTuningCommand.ExecuteAsync(null);

        await h.Safety.DidNotReceive().EnsureRestorePointAsync();
        await h.GpuStep.DidNotReceive().ApplyAsync(
            Arg.Any<ScanProfileConsumption>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingApplicable");
        h.Nav.DidNotReceive().NavigateToDashboard(Arg.Any<object>());
    }
}
