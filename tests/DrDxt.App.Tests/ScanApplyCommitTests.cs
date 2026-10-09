using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App;
using DrDxt.App.Navigation;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// M4/M5 — after a VALIDATED apply the App COMMITS a signed desired-state manifest so
// the reconciler can re-assert it at boot (the persistence moat, §9.12).
//
//   * the kept (Applied && !AutoReverted) tweaks are committed — exactly those, mapped
//     to their reconciler ids + desired tokens;
//   * an auto-reverted tweak is EXCLUDED;
//   * a commit FAILURE never breaks the apply UX: apply still succeeds, navigates to the
//     dashboard, and surfaces a non-fatal "moat unarmed" signal (localized resx key);
//   * a non-reconcilable / unmapped tweak is filtered (the mapper + the store both drop
//     it), so commit is not even attempted when nothing maps.
//
// Every seam is a fake — NO real signing key, NO real registry, NO real reconciler.
// -----------------------------------------------------------------------
public sealed class ScanApplyCommitTests
{
    private const int Score = 68;

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

    private static ApplyOutcome Kept(string id, string originalJson = "{}")
        => new(id, Applied: true, AutoReverted: false, new TweakSnapshot(id, originalJson), Error: null);

    private static ApplyOutcome AutoReverted(string id)
        => new(id, Applied: true, AutoReverted: true, new TweakSnapshot(id, "{}"), Error: null);

    private static ApplyReport ReportOf(params ApplyOutcome[] outcomes)
        => new(outcomes.ToList(), new AbVerdict(Bl, Bl, false, "verdict.no_measurable_gain"));

    private sealed class Harness
    {
        public required ScanViewModel Vm { get; init; }
        public required IDesiredStateStore Store { get; init; }
        public required INavigationService Nav { get; init; }
        public required DashboardViewModel Dashboard { get; init; }
    }

    private static Harness Build(
        Result<ApplyReport>? applyResult = null,
        Result? commitResult = null)
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
        safety.EnsureRestorePointAsync().Returns(Result.Ok());

        var engine = Substitute.For<ITweakEngine>();
        engine.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(applyResult ?? Result<ApplyReport>.Ok(ReportOf(Kept("fixit.power_plan"))));

        // M6 — a profile consumer with a non-empty plan so apply proceeds; the commit logic
        // under test depends on the engine's ApplyReport (the kept tweaks), not the plan ids.
        var consumer = ScanViewModelTestSeams.ProfileConsumer(
            ScanViewModelTestSeams.PlanConsumption(new[] { "latency.hagsOn" }));

        var store = Substitute.For<IDesiredStateStore>();
        store.CommitAsync(Arg.Any<DesiredState>()).Returns(commitResult ?? Result.Ok());
        // Finding-1 moat-entitlement refresh (end of scan) is a clean no-op here: nothing is
        // armed yet at scan time, so the only commit asserted below is the apply-time commit.
        store.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));

        var nav = Substitute.For<INavigationService>();
        var dashboard = new DashboardViewModel();

        var vm = new ScanViewModel(
            hd, sr, br, sc, sd, safety, engine, consumer, ScanViewModelTestSeams.GpuTuningStep(),
            nav, () => dashboard, store, new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key);

        return new Harness { Vm = vm, Store = store, Nav = nav, Dashboard = dashboard };
    }

    private static async Task ScanThenApply(Harness h)
    {
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyCommand.ExecuteAsync(null);
    }

    // A validated apply commits exactly the kept tweaks (mapped to reconciler ids).
    [Fact]
    public async Task Validated_apply_commits_exactly_the_kept_tweaks()
    {
        var h = Build(applyResult: Result<ApplyReport>.Ok(ReportOf(
            Kept("fixit.power_plan"),
            Kept("latency.reflex"))));

        await ScanThenApply(h);

        await h.Store.Received(1).CommitAsync(Arg.Is<DesiredState>(d =>
            d.Tweaks.Select(t => t.TweakId).OrderBy(x => x).SequenceEqual(
                new[] { "fixit.sanePowerPlan", "latency.reflex.lowLatencyMode" })));
        h.Vm.MoatUnarmedMessage.Should().BeNullOrEmpty("the commit succeeded → moat armed");
    }

    // An auto-reverted tweak is not committed (only the kept one is).
    [Fact]
    public async Task Auto_reverted_tweak_is_excluded_from_the_commit()
    {
        var h = Build(applyResult: Result<ApplyReport>.Ok(ReportOf(
            Kept("fixit.power_plan"),
            AutoReverted("latency.reflex"))));

        await ScanThenApply(h);

        await h.Store.Received(1).CommitAsync(Arg.Is<DesiredState>(d =>
            d.Tweaks.Count == 1 && d.Tweaks[0].TweakId == "fixit.sanePowerPlan"));
    }

    // A commit FAILURE never breaks apply: navigation still happens, no blocking message,
    // and a non-fatal "moat unarmed" signal is surfaced (localized key).
    [Fact]
    public async Task Commit_failure_leaves_apply_successful_but_flags_moat_unarmed()
    {
        var h = Build(commitResult: Result.Fail(ErrorCodes.IoFailure, "disk full"));

        await ScanThenApply(h);

        h.Vm.ResultatOptimisation.Should().NotBeNullOrEmpty("apply succeeded and says what it set");
        h.Vm.ApplyBlockedMessage.Should().BeNullOrEmpty("apply itself succeeded — nothing is blocked");
        h.Vm.MoatUnarmedMessage.Should().Be("Apply.MoatUnarmed",
            "a commit failure surfaces a non-fatal localized 'moat not armed' signal");
        // La premisse, affirmee. Le `catch` generique de CommitDesiredStateAsync ecrit exactement
        // le meme message pour n'importe quelle panne de frontiere : sans cette ligne, le test
        // restait vert alors que le commit n'avait jamais eu lieu. Mesure du 11/09/2026 : un
        // `throw` place avant `ToDesiredTweaks` laissait ce test vert et rougissait ses deux voisins.
        await h.Store.Received(1).CommitAsync(Arg.Any<DesiredState>());
    }

    // When nothing maps to a reconcilable surface, no manifest is committed (the moat has
    // nothing to arm) — but apply still succeeds and navigates.
    [Fact]
    public async Task No_reconcilable_kept_tweaks_commits_nothing_but_apply_succeeds()
    {
        // The engine kept only a GPU tweak (unmapped / not reconcilable).
        var h = Build(applyResult: Result<ApplyReport>.Ok(ReportOf(Kept("gpu.undervolt"))));

        await ScanThenApply(h);

        await h.Store.DidNotReceive().CommitAsync(Arg.Any<DesiredState>());
        h.Vm.ResultatOptimisation.Should().NotBeNullOrEmpty("apply succeeded and says what it set");
        h.Vm.ApplyBlockedMessage.Should().BeNullOrEmpty();
        // Ce qui distingue le `return` DU `catch`. Les deux laissent le magasin intact ; seul le
        // `catch` ecrit « moat unarmed ». Rien a armer n'est pas une panne d'armement, et sans cette
        // ligne le test ne savait pas dire par quelle porte le commit n'avait pas eu lieu.
        h.Vm.MoatUnarmedMessage.Should().BeNullOrEmpty("nothing was reconcilable — that is not a failure to arm");
    }

    // 06/10/2026 (plan 4.3) : un levier de la session Windows (HKCU) gardé n'est pas réaffirmé par le service,
    // mais son origine entre au manifeste en RevertOnly, le seul que la désinstallation relit. Un seul envoi pour
    // toutes les origines (une invite UAC au plus) ; un refus est dit, levier par levier.
    private static string OrigineVrr => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser,
        @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", RegistryValueType.String,
        "SwapEffectUpgradeEnable=1;")).Value!;

    private static ApplyOutcome VrrGarde => new("latency.vrrFlipModelOn", Applied: true, AutoReverted: false,
        new TweakSnapshot("latency.vrrFlipModelOn", OrigineVrr, PrivilegedOpCodec.Serialize(new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings",
            RegistryValueType.String, "SwapEffectUpgradeEnable=1;VRROptimizeEnable=1")).Value), Error: null);

    [Fact]
    public async Task Un_levier_de_session_garde_inscrit_son_origine_pour_la_desinstallation()
    {
        var h = Build(applyResult: Result<ApplyReport>.Ok(ReportOf(
            Kept("fixit.power_plan"),
            VrrGarde)));

        await ScanThenApply(h);

        await h.Store.Received(1).CommitAsync(Arg.Is<DesiredState>(d =>
            d.Tweaks.Count == 0 && d.RevertOnly!.Single().TweakId == "latency.vrrFlipModelOn"
            && d.RevertOnly!.Single().OriginalValueJson == OrigineVrr));
        h.Vm.MoatUnarmedMessage.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Une_origine_que_le_manifeste_refuse_est_dite()
    {
        var h = Build(applyResult: Result<ApplyReport>.Ok(ReportOf(VrrGarde)),
            commitResult: Result.Fail(ErrorCodes.IoFailure, "disk full"));

        await ScanThenApply(h);

        await h.Store.Received(1).CommitAsync(Arg.Any<DesiredState>());
        h.Vm.MoatUnarmedMessage.Should().Contain("Optimize.KeptOriginNotSigned").And.Contain("disk full");
    }

    // A failed apply never commits (nothing was validated).
    [Fact]
    public async Task Failed_apply_never_commits()
    {
        var h = Build(applyResult: Result<ApplyReport>.Fail(ErrorCodes.BatchReverted, "boom"));

        await ScanThenApply(h);

        await h.Store.DidNotReceive().CommitAsync(Arg.Any<DesiredState>());
        h.Vm.ApplyBlockedMessage.Should().NotBeNullOrEmpty();
    }
}
