using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// La commande d'application du réglage GPU, isolée et en option, sur le ScanViewModel.
//
// ApplyGpuTuningCommand est une action séparée de l'application sûre :
//   * désactivée tant qu'un scan n'a pas produit de résultats et que le profil n'a pas recommandé
//     une étape GPU applicable (carte non supportée → pas de plan GPU → reste désactivée ; jamais d'OC appliqué en silence) ;
//   * joue d'abord la porte du point de restauration, puis la session mesurée en jeu ; elle ne
//     joue jamais le plan sûr hors GPU.
//
// Le bouton passe desormais par le meme jugement que l'Optimiser, avant
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

    // Une consommation avec un plan GPU (pour que l'action GPU en option soit proposée). Le plan sûr
    // peut être vide : l'action GPU en est indépendante.
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
    }

    private static Harness Build(
        ScanProfileConsumption? consumption = null,
        Result? restoreResult = null)
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
              new List<Finding> { new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12) }, Bl)));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        var safety = Substitute.For<ISafetyManager>();
        safety.EnsureRestorePointAsync().Returns(restoreResult ?? Result.Ok());

        // Le moteur du lot sûr ne doit jamais être appelé par l'action GPU.
        var engine = ScanViewModelTestSeams.Engine();

        var consumer = ScanViewModelTestSeams.ProfileConsumer(consumption ?? WithGpuPlan());

        var store = ScanViewModelTestSeams.DesiredStateStore();

        var vm = new ScanViewModel(
            hd, sr, br, sc, sd, safety, engine, consumer,
            store, new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key);

        return new Harness
        {
            Vm = vm, Safety = safety, Engine = engine,
        };
    }

    // ====================== activation (en option / carte non supportée) ======================

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
        // Carte non supportée ou profil GPU seul écarté → plan GPU vide → l'action ne fait rien
        // et doit rester désactivée (jamais d'OC appliqué en silence).
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

    // ====================== porte de restauration → étape dédiée (isolement) ======================

    [Fact]
    public async Task Tant_que_la_decision_tient_le_bouton_GPU_n_atteint_ni_la_carte_ni_la_restauration_ni_le_moteur()
    {
        var h = Build(consumption: WithGpuPlan());
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyGpuTuningCommand.ExecuteAsync(null);

        await h.Safety.DidNotReceive().EnsureRestorePointAsync();
        // L'action GPU ne joue jamais le lot sûr hors GPU.
        await h.Engine.DidNotReceive().ApplyAsync(
            Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingThisRound");
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.Discarded · tweak.gpu.undervolt.title");
    }

    [Fact]
    public async Task Le_jugement_passe_avant_la_restauration_meme_quand_elle_serait_refusee()
    {
        var h = Build(restoreResult: Result.Fail(ErrorCodes.RestoreDisabled, "off"));
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyGpuTuningCommand.ExecuteAsync(null);

        await h.Safety.DidNotReceive().EnsureRestorePointAsync();
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingThisRound");
    }

    // ====================== outcomes ======================

    [Fact]
    public async Task Rien_a_tenter_ne_touche_pas_la_carte()
    {
        // Le jugement passe d'abord ; rien de retenu ⇒ rien ne s'ecrit.
        var h = Build(consumption: WithGpuPlan());
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyGpuTuningCommand.ExecuteAsync(null);

        await h.Safety.DidNotReceive().EnsureRestorePointAsync();
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingThisRound");
    }
}
