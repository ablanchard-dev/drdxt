using DrDxt.Application.Engine;
using DrDxt.Application.Safety;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// =======================================================================
// La porte d'isolement : « un échec GPU n'annule jamais le lot sûr ».
//
// Deux lots du moteur restent indépendants : un plan GPU joué par son propre engine.ApplyAsync
// ne remonte jamais dans le lot appliqué avant lui. Avec le vrai TweakEngine, le vrai SafetyManager
// et le vrai catalogue de vrais leviers (seule la frontière SDK est simulée par FakeSystemState +
// FakeElevatedClient + des coutures de lecture qui rapportent cet état), on modélise exactement
// ce flux : appliquer le lot sûr (gardé), puis jouer le plan GPU comme un second lot
// indépendant, et vérifier :
//   1. quand le lot GPU échoue en pleine application (écriture refusée), le moteur n'annule en
//      LIFO que le lot GPU ; le lot sûr appliqué avant est identique à l'octet près à son état gardé ;
//   2. quand le lot GPU est gardé puis régresse, le moteur n'annule que le changement GPU
//      (le filet de retour sur régression mesurée) ; le lot sûr reste intact, là encore.
public sealed class GpuTuningIsolationTests
{
    private const int Pid = 4242;

    private static readonly BenchmarkResult Baseline =
        new(200, 150, 120, new[] { 5.0, 5.0 }, 15, MarginOfError: 1, Runs: 1);

    // « après » == départ → ni régression ni gain → le lot est gardé.
    private sealed class NeutralBenchmark : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct) =>
            Task.FromResult(Result<BenchmarkResult>.Ok(
                new BenchmarkResult(200, 150, 120, new[] { 5.0, 5.0 }, 15, MarginOfError: 1, Runs: 1)));
    }

    // « après » bien pire que le départ → régression mesurée → le moteur annule tout seul.
    private sealed class RegressionBenchmark : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct) =>
            Task.FromResult(Result<BenchmarkResult>.Ok(
                new BenchmarkResult(120, 80, 60, new[] { 8.0, 8.1 }, 25, MarginOfError: 1, Runs: 1)));
    }

    private sealed class EnabledRestore : IRestorePointService
    {
        public bool IsEnabled() => true;
        public bool TryEnable() => true;
        public DateTime? DernierPointDepuis(DateTime depuisUtc) => DateTime.UtcNow;
    }

    private sealed class GrantingConsent : IConsentPrompt
    {
        public bool AskEnableRestore() => true;
    }

    private sealed class NoOpDiagnostics : IDiagnosticsLog
    {
        public void Change(string tweakId, string from, string to, string actor) { }
        public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(string.Empty));
    }

    // Un départ NVIDIA de bureau : décalages et puissance GPU d'origine, plan Équilibré et une
    // graine de registre reflex capturée, pour que le lot sûr (plan + reflex) s'applique et se
    // capture, et que le lot GPU (limite de puissance + undervolt) ait des origines où revenir.
    private static FakeSystemState NewBaseline()
    {
        var s = new FakeSystemState
        {
            NvApiAvailable = true,
            ActivePowerPlan = KnownPowerPlans.Balanced,
            NvLowLatencyMode = 0,
            GpuCoreOffsetMhz = 0,
            GpuMemoryOffsetMhz = 0,
            GpuPowerLimitPct = 100,
        };
        s.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode", 0);
        return s;
    }

    // Le plan sûr (hors GPU) et le plan GPU séparé, miroir de ce que produit ScanProfileConsumer
    // (Plan et GpuPlan).
    private static readonly TweakPlan SafePlan = new(new[] { "fixit.power_plan", "latency.reflex" });
    private static readonly TweakPlan GpuPlan = new(new[] { "gpu.power_limit", "gpu.undervolt" });

    private static TweakEngine BuildEngine(
        FakeSystemState state, IBenchmarkRunner bench, Func<PrivilegedOp, bool>? refuse, out FakeElevatedClient client)
    {
        client = new FakeElevatedClient(state, refuse);
        var safety = new SafetyManager(new EnabledRestore(), new GrantingConsent(), client);
        var diag = new NoOpDiagnostics();

        var nv = new FakeNvApi(state);
        var reg = new FakeRegistryReader(state);
        var power = new FakePowerPlanReader(state);
        var nvTuning = new FakeNvApiGpuTuning(state);
        var adlxTuning = new FakeAdlxGpuTuning(state);
        // Les cibles GPU prudentes, dans les bornes (les mêmes que celles de l'App).
        var gpuTargets = new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, 90));

        var tweaks = new ITweak[]
        {
            new SanePowerPlanTweak(power, client),
            new NvidiaUltraLowLatencyTweak(nv, reg, client),
            new GpuPowerLimitTweak(nvTuning, adlxTuning, client, gpuTargets),
            new GpuUndervoltTweak(nvTuning, adlxTuning, new StableStabilityTester(), client, gpuTargets),
        };
        var catalog = new TweakCatalog(tweaks);
        return new TweakEngine(safety, bench, diag, catalog);
    }

    // Applique le lot sûr et vérifie qu'il a été gardé (pas annulé automatiquement) ; rend son instantané.
    private static async Task<string> ApplyKeptSafeBatchAsync(FakeSystemState state)
    {
        var engine = BuildEngine(state, new NeutralBenchmark(), refuse: null, out _);
        var safe = await engine.ApplyAsync(SafePlan, Baseline, Pid, default);

        safe.IsOk.Should().BeTrue();
        safe.Value!.Outcomes.Should().OnlyContain(o => o.Applied && !o.AutoReverted);
        // Le lot sûr a vraiment changé la machine (pour que la porte ci-dessous ne soit pas creuse).
        state.ActivePowerPlan.Should().Be(KnownPowerPlans.HighPerformance);
        return state.Snapshot();
    }

    // =================== Porte 1 : un échec d'application GPU laisse le lot sûr intact ===================

    [Fact]
    public async Task Gpu_batch_apply_failure_does_not_roll_back_the_previously_applied_safe_batch()
    {
        var state = NewBaseline();
        var before = state.Snapshot();

        var afterSafe = await ApplyKeptSafeBatchAsync(state);
        afterSafe.Should().NotBe(before); // témoin : le lot sûr est appliqué et gardé.

        // Le lot GPU tourne comme un appel séparé du moteur. Refuser l'écriture du décalage
        // d'horloge de l'undervolt pour que le lot GPU échoue après l'écriture de la limite de
        // puissance → le moteur annule le lot GPU en LIFO (limite de puissance rendue à l'origine).
        static bool RefuseUndervoltWrite(PrivilegedOp op) =>
            op is ApplyNvApiClockOffsetOp { CoreOffsetMhz: -100 };

        var gpuEngine = BuildEngine(state, new NeutralBenchmark(), RefuseUndervoltWrite, out var gpuClient);
        var gpu = await gpuEngine.ApplyAsync(GpuPlan, Baseline, Pid, default);

        gpu.IsOk.Should().BeFalse();
        gpu.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);
        // Le lot GPU a bien écrit (limite de puissance) avant d'être annulé → la porte n'est pas creuse.
        gpuClient.Writes.Should().NotBeEmpty();

        // La porte : le lot sûr est identique à l'octet près à son état gardé, jamais annulé.
        state.Snapshot().Should().Be(afterSafe);
        state.ActivePowerPlan.Should().Be(KnownPowerPlans.HighPerformance); // le correctif sûr survit
        // Et le changement GPU lui-même a été entièrement annulé (seulement lui).
        state.GpuPowerLimitPct.Should().Be(100);
        state.GpuCoreOffsetMhz.Should().Be(0);
    }

    // =================== Porte 2 : une régression GPU mesurée n'annule que le changement GPU ===================

    [Fact]
    public async Task Gpu_batch_measured_regression_auto_reverts_only_the_gpu_change()
    {
        var state = NewBaseline();
        var afterSafe = await ApplyKeptSafeBatchAsync(state);

        // Le lot GPU s'applique en entier (stable), puis un banc en régression fait annuler
        // automatiquement tout le lot GPU par le moteur : le filet de retour sur régression mesurée.
        var gpuEngine = BuildEngine(state, new RegressionBenchmark(), refuse: null, out _);
        var gpu = await gpuEngine.ApplyAsync(GpuPlan, Baseline, Pid, default);

        gpu.IsOk.Should().BeTrue();
        gpu.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted");
        gpu.Value.Outcomes.Should().OnlyContain(o => o.AutoReverted);

        // La porte : le lot sûr est identique à l'octet près à son état gardé ; seul le GPU est revenu.
        state.Snapshot().Should().Be(afterSafe);
        state.ActivePowerPlan.Should().Be(KnownPowerPlans.HighPerformance);
        state.GpuPowerLimitPct.Should().Be(100);
        state.GpuCoreOffsetMhz.Should().Be(0);
    }
}
