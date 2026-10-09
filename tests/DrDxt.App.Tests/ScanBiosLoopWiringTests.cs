using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Platform;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// L0.2 — LE CABLAGE, pas la logique.
//
// La logique du verifieur et du marqueur etait deja testee depuis M3. Ce qui manquait n'etait
// pas un test de plus sur elle : c'est que personne ne l'appelait. Ces tests-la echouent si le
// scan cesse de prononcer le verdict, ou si le bouton « emmene-moi dans le BIOS » redevient
// decoratif -- les deux facons exactes dont ce differenciateur est deja mort une fois.
// -----------------------------------------------------------------------
public sealed class ScanBiosLoopWiringTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "Intel", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "NVIDIA", 8L << 30, "1", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 4000, 3800) },
        new MotherboardInfo("MSI", "PRO B760-P"),
        new BiosInfo("1.H0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.Off, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static BiosFinding AdvisorFinding() =>
        new(FirmwareSettingKind.XmpExpo, Severity.Major, "XmpExpo.Title", "XmpExpo.Rationale",
            BiosMatrixClass.Guide, TriState.Off, BiosRecommendedAction.Enable, "XmpExpo.Action",
            Gain: null, StateNotReadable: false);

    private sealed class FakeTrip : IFirmwareTrip
    {
        private readonly Result<BiosLoopResult> _verdict;
        public FakeTrip(Result<BiosLoopResult>? verdict = null)
            => _verdict = verdict ?? Result<BiosLoopResult>.Ok(BiosLoopResult.NothingPending);

        public IReadOnlyList<BiosFinding>? ArmedWith { get; private set; }
        public SettingsState? ArmedState { get; private set; }
        public int ArmCount { get; private set; }
        public SettingsState? VerifiedAgainst { get; private set; }

        public Task<Result> ArmAndRebootAsync(
            IReadOnlyList<BiosFinding> findings, SettingsState before, int delaySeconds, CancellationToken ct)
        {
            ArmCount++;
            ArmedWith = findings;
            ArmedState = before;
            return Task.FromResult(Result.Ok());
        }

        public Result<BiosLoopResult> VerifyOnReturn(SettingsState fresh)
        {
            VerifiedAgainst = fresh;
            return _verdict;
        }
    }

    private static ScanViewModel BuildWith(IFirmwareTrip? trip)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(
              new BenchmarkResult(138.9, 98.3, 69.6, new List<double> { 7.0 }, null, 2.1, 5)));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), 72, null,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(true, false))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        return ScanViewModelTestSeams.Build(
            hd, sr, br, sc, sd, key => key,
            ScanViewModelTestSeams.BiosAdvisor(AdvisorFinding()),
            ScanViewModelTestSeams.GuideGenerator(
                ScanViewModelTestSeams.Guide(steps: ScanViewModelTestSeams.Step())),
            trip);
    }

    [Fact]
    public async Task The_scan_asks_for_the_verdict_against_the_state_it_just_read()
    {
        var trip = new FakeTrip();

        await BuildWith(trip).ScanCommand.ExecuteAsync(null);

        trip.VerifiedAgainst.Should().Be(St);
    }

    [Fact]
    public async Task Nothing_pending_shows_no_section_at_all()
    {
        // Presque toutes les ouvertures de l'app ne suivent aucun voyage dans le BIOS : une
        // section vide ne doit pas s'afficher, et surtout pas un « tout va bien » fabrique.
        var vm = BuildWith(new FakeTrip());

        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosLoop.Should().BeNull();
        vm.HasBiosLoopResult.Should().BeFalse();
    }

    [Fact]
    public async Task A_change_that_did_not_take_is_surfaced_with_its_recovery_card()
    {
        var outcome = new BiosSettingOutcome(
            FirmwareSettingKind.XmpExpo, TriState.On, TriState.Off, BiosSettingVerdict.DidNotTake);
        var vm = BuildWith(new FakeTrip(Result<BiosLoopResult>.Ok(new BiosLoopResult(
            HadPendingChange: true, AllTook: false, Outcomes: new[] { outcome },
            ShowRecoveryCard: true, RecoveryCardKeys: new[] { "Bios.Loop.Recovery.Header" },
            MarkerCleared: false))));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasBiosLoopResult.Should().BeTrue();
        vm.BiosLoop!.AllTook.Should().BeFalse();
        vm.BiosLoop.Headline.Should().BeEmpty("on ne titre rien tant que tout n'est pas confirme");
        vm.BiosLoop.ShowRecoveryCard.Should().BeTrue();
        vm.BiosLoop.Outcomes.Should().ContainSingle()
            .Which.VerdictLabel.Should().Be("Bios.Loop.Verdict.DidNotTake");
    }

    [Fact]
    public async Task An_unverifiable_setting_is_never_sold_as_a_success()
    {
        // « Je n'ai pas pu verifier » n'est ni « ca a marche » ni « ca a echoue ». Le confondre
        // avec le premier est le mensonge que toute cette boucle existe pour eviter.
        var outcome = new BiosSettingOutcome(
            FirmwareSettingKind.Csm, TriState.Off, TriState.Unknown, BiosSettingVerdict.Unreadable);
        var vm = BuildWith(new FakeTrip(Result<BiosLoopResult>.Ok(new BiosLoopResult(
            HadPendingChange: true, AllTook: false, Outcomes: new[] { outcome },
            ShowRecoveryCard: true, RecoveryCardKeys: new[] { "Bios.Loop.Recovery.Header" },
            MarkerCleared: false))));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosLoop!.AllTook.Should().BeFalse();
        vm.BiosLoop.Outcomes.Should().ContainSingle()
            .Which.VerdictLabel.Should().Be("Bios.Loop.Verdict.Unreadable");
    }

    [Fact]
    public async Task The_guide_button_actually_reaches_the_trip_with_this_scans_findings()
    {
        // Le defaut d'origine, dans sa forme exacte : la couture existait, etait testee, et
        // ScanViewModel construisait le guide sans elle. Le bouton etait decoratif.
        var trip = new FakeTrip();
        var vm = BuildWith(trip);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosGuide!.CanOpenFirmwareDirectly.Should().BeTrue();

        var r = await vm.BiosGuide.OpenFirmwareSetupAsync();

        r.IsOk.Should().BeTrue();
        trip.ArmCount.Should().Be(1);
        trip.ArmedWith.Should().ContainSingle().Which.Kind.Should().Be(FirmwareSettingKind.XmpExpo);
        trip.ArmedState.Should().Be(St, "l'utilisateur repart avec l'etat qu'il a sous les yeux");
    }

    [Fact]
    public async Task Without_the_seam_the_manual_path_stays_and_no_verdict_is_invented()
    {
        var vm = BuildWith(trip: null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosGuide!.CanOpenFirmwareDirectly.Should().BeFalse();
        vm.BiosGuide.EnterInstruction.Should().NotBeEmpty(
            "le chemin manuel reste affiche : un produit qui cache la touche parce qu'il croit "
            + "avoir mieux laisse le client sans rien le jour ou le mieux ne marche pas");
        vm.BiosLoop.Should().BeNull();
    }
}
