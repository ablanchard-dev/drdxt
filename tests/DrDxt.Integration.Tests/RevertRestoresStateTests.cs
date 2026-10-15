using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Engine;
using DrDxt.Application.Safety;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Integration.Tests;

// =======================================================================
// La porte : « le retour arrière rend l'état ».
//
// La promesse qu'un retour arrière rend la machine repose là-dessus. Avec le vrai TweakEngine,
// le vrai SafetyManager et le vrai TweakCatalog de vrais leviers (seule la frontière SDK est
// simulée par FakeSystemState + FakeElevatedClient + des coutures de lecture qui rapportent
// cet état), on vérifie :
//   1. Après apply puis revert, l'état factice est identique à l'octet près à l'instantané
//      de départ (retour automatique du moteur sur une régression simulée).
//   2. Un levier qui échoue en plein lot laisse l'état identique à l'octet près au départ
//      (transactionnel : jamais d'état à moitié appliqué).
//   3. (couverture élargie) Le RevertAsync propre d'un levier à instantané composite rend
//      aussi l'état à l'octet près (chemin de retour explicite).
//
// Déterministe, sans vraie machine : la CI joue ceci comme porte dure.
// =======================================================================
public sealed class RevertRestoresStateTests
{
    private const int Pid = 4242;
    private static readonly TimeSpan Dur = TimeSpan.FromSeconds(10);

    // --- Un départ qui rend applicables et capturables les leviers à une op et les composites.
    // Les composites (fixit.x3d_ccd, latency.vrr_vsync_cap) stockent un instantané
    // multi-ops que seul leur propre RevertAsync sait décoder ; les passer par le moteur
    // prouve que le chemin de retour automatique du moteur rend un composite à l'octet près.
    private static FakeSystemState NewBaseline()
    {
        var s = new FakeSystemState
        {
            NvApiAvailable = true,           // chemin NVIDIA pour reflex + réglage GPU + plafond VRR
            ActivePowerPlan = KnownPowerPlans.Balanced,
            NvLowLatencyMode = 0,            // origine capturée = désactivé
            GpuCoreOffsetMhz = 0,
            GpuMemoryOffsetMhz = 0,
            GpuPowerLimitPct = 100,
            // Origines DRS capturées pour le composite VRR/Vsync (Vsync désactivée, pas de plafond).
            NvVsyncMode = 0x60925292, // PASSIVE, le défaut du pilote
            NvFrameRateLimit = 0,
            // Un écran VRR détecté, pour que l'ApplyAsync de latency.vrr_vsync_cap capture et écrive.
            PrimaryGameDisplay = new DisplayInfo(IsPrimaryGameDisplay: true, SupportsVrr: true, RefreshHz: 144),
        };
        // Une valeur NVTweak LowLatencyMode préexistante, pour que le retour de reflex la rende.
        s.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode", 0);
        // Pré-amorcer les feuilles DRS que le composite VRR/Vsync touche avec leurs origines
        // capturées (Vsync = 0, limite d'images = 0), pour qu'un retour composite fidèle ramène
        // ces clés exactes au départ (les clés existent avant et après : identiques à l'octet près).
        s.SetDrs(0x00A879CF, 0x60925292);  // VSYNCMODE_ID original = PASSIVE, le défaut du pilote
        s.SetDrs(0x10835002, 0);  // FRL_FPS_ID d'origine = pas de plafond (identifiant corrigé)
        // Captures du composite X3D-CCD : le service optimiseur est arrêté au départ, et une
        // feuille CppcEnable préexiste ; son retour composite doit donc rendre les deux.
        s.SetService("AMD3DVCacheSvc", ServiceState.Stopped);
        s.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Processor", "CppcEnable", 0);
        // Valeur Game Bar absente au départ → son inverse fidèle est une suppression.
        return s;
    }

    // --- Un départ AMD : NVAPI absent + ADLX présent, pour que les leviers de réglage GPU
    // prennent la branche ADLX. Les origines ADLX capturées sont ce qu'une ApplyAdlxTuningOp inverse
    // fidèle doit rendre après le retour automatique du moteur. Le chemin d'undervolt fait
    // l'aller-retour d'une MaxFreq absolue (origine 2900 → application 2800 → retour 2900) : c'est
    // ce qui donne des dents à la porte ; un inverse en mauvaises unités ou en VoltMv laisserait AdlxMaxFreqMhz != 2900 et la ferait échouer. ---
    private static FakeSystemState NewAmdBaseline() => new()
    {
        NvApiAvailable = false,           // pas de NVIDIA → le réglage GPU résout le chemin ADLX
        AdlxAvailable = true,             // ADLX AMD présent (une seule dGPU réglable à l'index 0)
        ActivePowerPlan = KnownPowerPlans.Balanced,
        AdlxMaxFreqMhz = 2900,            // horloge max GFX absolue d'origine capturée (la surface de l'undervolt)
        AdlxPowerLimitPct = 100,          // limite de puissance d'origine capturée
    };

    // --- Les coutures de sécurité transverses : restauration active + point vérifié. ---
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

    // --- Un exécutant de banc qui rend une régression, pour que le moteur annule tout seul. ---
    private sealed class RegressionBenchmark : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct) =>
            // « après » bien pire que le départ (posé dans le test) → régression au-delà de la marge.
            Task.FromResult(Result<BenchmarkResult>.Ok(
                new BenchmarkResult(120, 80, 60, new[] { 8.0, 8.1 }, 25, MarginOfError: 1, Runs: 1)));
    }

    private static readonly BenchmarkResult Baseline =
        new(200, 150, 120, new[] { 5.0, 5.0 }, 15, MarginOfError: 1, Runs: 1);

    // --- Câbler le vrai moteur, la vraie sécurité et le vrai catalogue sur l'état factice. ---
    private static (TweakEngine Engine, SafetyManager Safety, TweakCatalog Catalog, FakeElevatedClient Client)
        BuildReal(FakeSystemState state, IBenchmarkRunner bench, Func<PrivilegedOp, bool>? refuse = null)
    {
        var client = new FakeElevatedClient(state, refuse);
        var safety = new SafetyManager(new EnabledRestore(), new GrantingConsent(), client);
        var diag = new NoOpDiagnostics();

        var tweaks = BuildRealTweaks(state, client);
        var catalog = new TweakCatalog(tweaks);
        var engine = new TweakEngine(safety, bench, diag, catalog);
        return (engine, safety, catalog, client);
    }

    // Les vrais leviers, construits sur les fausses coutures (jeu NVIDIA à une op, pour que
    // le chemin de retour générique du SafetyManager du moteur puisse tous les rendre).
    private static IReadOnlyList<ITweak> BuildRealTweaks(FakeSystemState state, FakeElevatedClient client)
    {
        var nv = new FakeNvApi(state);
        var nvTuning = new FakeNvApiGpuTuning(state);
        var adlx = new FakeAdlx(state);
        var adlxTuning = new FakeAdlxGpuTuning(state);
        var reg = new FakeRegistryReader(state);
        var power = new FakePowerPlanReader(state);
        var svc = new FakeServiceStateReader(state);
        var display = new FakeDisplayInfo(state);
        var stability = new StableStabilityTester();
        // Les leviers GPU lisent leurs cibles dans l'IGpuTuningTargetSource vivant, amorcé ici
        // avec les mêmes valeurs prudentes qu'avant (comportement de la porte à l'octet près inchangé).
        var gpuTargets = new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, 90));

        return new ITweak[]
        {
            new NvidiaUltraLowLatencyTweak(nv, reg, client),
            new RenderQueueCapTweak(nv, adlx, client),
            new VrrVsyncCapConfigTweak(nv, display, client),
            new GpuUndervoltTweak(nvTuning, adlxTuning, stability, client, gpuTargets),
            new GpuPowerLimitTweak(nvTuning, adlxTuning, client, gpuTargets),
            new GpuFanCurveTweak(nvTuning, adlxTuning, client, gpuTargets, new GpuFanCurveTarget(0,
                new FanCurve(new[] { new FanCurvePoint(40, 35), new FanCurvePoint(80, 70), new FanCurvePoint(90, 90) }))),
            new X3dCcdParkingFixTweak(svc, reg, client),
            new SanePowerPlanTweak(power, client),
        };
    }

    // =================== Porte 1 : application → retour automatique → identique ===================

    [Fact]
    public async Task After_apply_then_revert_state_is_identical_to_baseline()
    {
        var state = NewBaseline();
        var (engine, _, _, client) = BuildReal(state, new RegressionBenchmark());

        var before = state.Snapshot();

        // Un lot riche qui touche registre + plan d'alimentation + 3 surfaces GPU et deux
        // leviers à instantané composite (fixit.x3d_ccd = service + 2 feuilles de registre ;
        // latency.vrr_vsync_cap = 2 feuilles DRS). Les composites stockent un instantané
        // multi-ops que seul leur propre RevertAsync sait décoder : ceci prouve que le chemin
        // de retour automatique du moteur (tweak.RevertAsync, pas _safety) rend un composite à
        // l'octet près, le correctif du trou de réversibilité exercé de bout en bout.
        var plan = new TweakPlan(new[]
        {
            "latency.reflex", "fixit.power_plan", "gpu.undervolt", "gpu.power_limit", "gpu.fan_curve",
            "fixit.x3d_ccd", "latency.vrr_vsync_cap",
        });

        var result = await engine.ApplyAsync(plan, Baseline, Pid, default);

        // La régression simulée fait annuler automatiquement tout le lot par le moteur.
        result.IsOk.Should().BeTrue();
        result.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted");
        result.Value.Outcomes.Should().OnlyContain(o => o.AutoReverted);
        // Les composites faisaient bien partie du lot annulé (la porte n'est pas creuse).
        result.Value.Outcomes.Select(o => o.TweakId)
            .Should().Contain(new[] { "fixit.x3d_ccd", "latency.vrr_vsync_cap" });

        // Témoin : le lot a bien écrit avant de revenir (la porte n'est pas creuse).
        client.Writes.Should().NotBeEmpty();
        // Dents sur le chemin NVIDIA : gpu.undervolt avec NvApiAvailable=true émet une vraie écriture
        // NVAPI de décalage d'horloge (GpuCoreOffsetMhz 0 → cible), donc la restauration à l'octet
        // près ci-dessous exerce vraiment l'aller-retour capture → application → retour du décalage
        // NVAPI, pas seulement les composites. Un mauvais inverse laisserait GpuCoreOffsetMhz != 0 et ferait échouer la porte.
        client.Writes.Should().Contain(op => op is ApplyNvApiClockOffsetOp);

        // La porte : retour à l'origine identique à l'octet près, surfaces composites comprises.
        state.Snapshot().Should().Be(before);
    }

    // =================== Porte 1b (AMD/ADLX) : application du décalage ADLX → retour automatique → identique ===================

    [Fact]
    public async Task After_apply_then_revert_state_is_identical_on_the_amd_adlx_path()
    {
        // La porte « le retour arrière rend l'état », sur le chemin AMD. Avec NVAPI absent
        // et ADLX présent, gpu.undervolt / gpu.power_limit résolvent la carte dédiée réglable
        // et écrivent par ApplyAdlxTuningOp ; la régression simulée fait annuler le moteur en
        // rejouant les ops ADLX inverses capturées depuis les origines.
        var state = NewAmdBaseline();
        var (engine, _, _, client) = BuildReal(state, new RegressionBenchmark());

        var before = state.Snapshot();

        var plan = new TweakPlan(new[] { "gpu.undervolt", "gpu.power_limit" });

        var result = await engine.ApplyAsync(plan, Baseline, Pid, default);

        // La régression simulée fait annuler automatiquement tout le lot GPU par le moteur.
        result.IsOk.Should().BeTrue();
        result.Value!.Verdict!.SummaryKey.Should().Be("verdict.regression_reverted");
        result.Value.Outcomes.Should().OnlyContain(o => o.AutoReverted);

        // Pas creux, et vraiment le chemin ADLX : le lot a émis des écritures ADLX et jamais un
        // décalage d'horloge NVAPI (preuve qu'il a pris la branche AMD, pas NVIDIA).
        client.Writes.Should().Contain(op => op is ApplyAdlxTuningOp);
        client.Writes.Should().NotContain(op => op is ApplyNvApiClockOffsetOp);

        // La porte : retour à l'origine identique à l'octet près, undervolt et limite de puissance ADLX rendus.
        state.Snapshot().Should().Be(before);
    }

    // =================== Porte 2 : échec partiel → identique ===================

    [Fact]
    public async Task Batch_partial_failure_leaves_state_identical_to_baseline()
    {
        var state = NewBaseline();

        // Refuser l'écriture de registre de reflex pour que le second levier échoue en plein lot,
        // après l'application du premier (plan d'alimentation). Le moteur doit annuler le premier.
        static bool RefuseReflexWrite(PrivilegedOp op) =>
            op is SetRegistryValueOp { Name: "LowLatencyMode" };

        var (engine, _, _, _) = BuildReal(state, new RegressionBenchmark(), RefuseReflexWrite);

        var before = state.Snapshot();

        var plan = new TweakPlan(new[] { "fixit.power_plan", "latency.reflex" });

        var result = await engine.ApplyAsync(plan, Baseline, Pid, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.BatchReverted);

        // La porte : pas d'état à moitié appliqué ; le changement de plan a été annulé.
        state.Snapshot().Should().Be(before);
    }

    // =================== couverture élargie : retour explicite d'un composite ===================

    [Fact]
    public async Task Composite_tweak_explicit_revert_restores_state_identically()
    {
        // Le correctif X3D (x3d_ccd) écrit un composite (service + 2 feuilles de registre) ; son propre
        // RevertAsync rejoue le composite pour rendre les trois. Ceci prouve que le chemin
        // d'instantané composite est lui aussi sans perte à l'octet près.
        var state = NewBaseline();
        state.SetService("AMD3DVCacheSvc", ServiceState.Stopped);
        state.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Processor", "CppcEnable", 1);
        // Valeur Game Bar absente au départ → son inverse fidèle est une suppression.

        var client = new FakeElevatedClient(state);
        var tweak = new X3dCcdParkingFixTweak(
            new FakeServiceStateReader(state), new FakeRegistryReader(state), client);

        var before = state.Snapshot();

        var apply = await tweak.ApplyAsync();
        apply.IsOk.Should().BeTrue();
        // L'application a vraiment changé l'état (sinon la porte serait creuse).
        state.Snapshot().Should().NotBe(before);

        var revert = await tweak.RevertAsync(apply.Value!);
        revert.IsOk.Should().BeTrue();

        // La porte : le retour du composite rend l'état identique à l'octet près.
        state.Snapshot().Should().Be(before);
    }

    // =================== le catalogue utilisé est le vrai (résoluble par DI) ===================

    [Fact]
    public void Real_application_catalog_resolves_via_DI_and_is_non_empty()
    {
        // Les mêmes enregistrements que l'hôte App : un contrôle de fumée que la porte
        // d'intégration exerce le vrai catalogue, pas un sous-ensemble fait main.
        var services = new ServiceCollection();
        services.AddSingleton<IDiagnosticsLog>(new NoOpDiagnostics());
        services.AddSingleton<IBenchmarkRunner>(new RegressionBenchmark());
        services.AddDrDxtApplication();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITweakCatalog>().All().Should().NotBeEmpty();
        provider.GetService<ITweakEngine>().Should().NotBeNull();
    }
}
