using System.Linq;
using System.Threading.Tasks;
using DrDxt.App.Navigation;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// M5 — wiring the dashboard "Revert all" to the real revert path.
//
// RevertCommand reverts every applied-and-kept snapshot through ISafetyManager
// (the standalone snapshot revert path), reflects the reverted state in the VM,
// and lets the user navigate back to the scan. Already-auto-reverted tweaks are
// not reverted again. Every seam is a fake — no real elevation, no real registry.
// -----------------------------------------------------------------------
public sealed class DashboardRevertCommandTests
{
    private static BenchmarkResult Bench(double avg) =>
        new(avg, avg * 0.8, avg * 0.6, new[] { 5.0, 5.0 }, 15, 1.0, 3);

    private static ApplyReport ReportWithApplied(params string[] keptIds)
    {
        var outcomes = keptIds
            .Select(id => new ApplyOutcome(id, Applied: true, AutoReverted: false,
                new TweakSnapshot(id, "{}"), Error: null))
            .ToList();
        var verdict = new AbVerdict(Bench(200), Bench(230), IsMeasurableGain: true, "verdict.measurable_gain");
        return new ApplyReport(outcomes, verdict);
    }

    private static (DashboardViewModel Vm, ISafetyManager Safety, INavigationService Nav) Build(
        ApplyReport report, Result? revertResult = null)
    {
        var safety = Substitute.For<ISafetyManager>();
        safety.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(revertResult ?? Result.Ok());
        var nav = Substitute.For<INavigationService>();

        var vm = new DashboardViewModel(safety, nav, () => new object(), key => key);
        vm.Load(report);
        return (vm, safety, nav);
    }

    [Fact]
    public void Revert_command_exists_and_is_enabled_when_there_are_kept_changes()
    {
        var (vm, _, _) = Build(ReportWithApplied("fixit.power_plan"));
        vm.RevertCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Revert_command_is_disabled_when_nothing_was_kept()
    {
        var (vm, _, _) = Build(ReportWithApplied(/* none */));
        vm.RevertCommand.CanExecute(null).Should().BeFalse();
    }

    // 04/10/2026 (plan 6.1) : chaque levier annulé avec succès passe par « après annulation » (manifeste, journal) ;
    // un échec d'annulation non.
    [Fact]
    public async Task Chaque_annulation_reussie_passe_par_apres_annulation()
    {
        var (vm, safety, _) = Build(ReportWithApplied("fixit.power_plan", "latency.hagsOn"));
        safety.RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "latency.hagsOn")).Returns(Result.Fail(ErrorCodes.HelperOpFailed, "refus"));
        var apres = new List<string>();
        vm.ApresAnnulation = id => { apres.Add(id); return Task.FromResult(Result.Ok()); };

        await vm.RevertCommand.ExecuteAsync(null);

        apres.Should().Equal("fixit.power_plan");
    }

    // 06/10/2026 (revue) : annulé sur la machine mais resté au manifeste signé = la désinstallation pourrait
    // remettre une vieille origine. Ça se dit, au lieu d'être avalé.
    [Fact]
    public async Task Un_levier_annule_qui_reste_au_manifeste_est_dit()
    {
        var safety = Substitute.For<ISafetyManager>();
        safety.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
        var vm = new DashboardViewModel(safety, Substitute.For<INavigationService>(), () => new object(),
            key => key == "Changement.PasSortiDuManifeste" ? "{0} resté au manifeste ({1})" : key);
        vm.Load(ReportWithApplied("latency.hagsOn"));
        vm.ApresAnnulation = _ => Task.FromResult(Result.Fail(ErrorCodes.ManifestUnreadable, "illisible"));

        await vm.RevertCommand.ExecuteAsync(null);

        vm.RevertErrorMessage.Should().Be($"{TweakDisplayKeys.TitleKey("latency.hagsOn")} resté au manifeste (illisible)");
    }

    [Fact]
    public async Task Revert_invokes_safety_revert_for_each_kept_snapshot()
    {
        var (vm, safety, _) = Build(ReportWithApplied("fixit.power_plan", "latency.reflex"));
        await vm.RevertCommand.ExecuteAsync(null);

        await safety.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "fixit.power_plan"));
        await safety.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "latency.reflex"));
    }

    [Fact]
    public async Task Successful_revert_moves_kept_ids_into_the_reverted_list()
    {
        var (vm, _, _) = Build(ReportWithApplied("fixit.power_plan"));
        await vm.RevertCommand.ExecuteAsync(null);

        vm.AppliedTweakIds.Should().BeEmpty();
        vm.RevertedTweakIds.Should().Contain("fixit.power_plan");
        vm.HasReverted.Should().BeTrue();
    }

    [Fact]
    public async Task Revert_failure_surfaces_a_message_and_keeps_state_consistent()
    {
        var (vm, _, _) = Build(ReportWithApplied("fixit.power_plan"),
            revertResult: Result.Fail(ErrorCodes.HelperOpFailed, "no helper"));
        await vm.RevertCommand.ExecuteAsync(null);

        vm.RevertErrorMessage.Should().NotBeNullOrEmpty();
        // The id that could not be reverted stays in the applied list (honest state).
        vm.AppliedTweakIds.Should().Contain("fixit.power_plan");
    }

    [Fact]
    public async Task Revert_does_not_re_revert_already_auto_reverted_tweaks()
    {
        var outcomes = new[]
        {
            new ApplyOutcome("fixit.power_plan", Applied: true, AutoReverted: false, new TweakSnapshot("fixit.power_plan", "{}"), Error: null),
            new ApplyOutcome("gpu.undervolt", Applied: true, AutoReverted: true, new TweakSnapshot("gpu.undervolt", "{}"), Error: null),
        };
        var report = new ApplyReport(outcomes, Verdict: null);
        var safety = Substitute.For<ISafetyManager>();
        safety.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
        var nav = Substitute.For<INavigationService>();
        var vm = new DashboardViewModel(safety, nav, () => new object(), key => key);
        vm.Load(report);

        await vm.RevertCommand.ExecuteAsync(null);

        // Only the kept tweak is reverted; the already-auto-reverted one is left alone.
        await safety.Received(1).RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "fixit.power_plan"));
        await safety.DidNotReceive().RevertAsync(Arg.Is<TweakSnapshot>(s => s.TweakId == "gpu.undervolt"));
    }

    [Fact]
    public void Back_to_scan_navigates_via_the_navigation_service()
    {
        var scanVm = new object();
        var safety = Substitute.For<ISafetyManager>();
        var nav = Substitute.For<INavigationService>();
        var vm = new DashboardViewModel(safety, nav, () => scanVm, key => key);
        vm.Load(ReportWithApplied("fixit.power_plan"));

        vm.BackToScanCommand.Execute(null);

        nav.Received(1).NavigateToScan(scanVm);
    }
}
