using DrDxt.Application.Engine;
using DrDxt.Application.Safety;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Application.DependencyInjection;

// -----------------------------------------------------------------------
// M2 H2 — application-layer DI wiring.
//
// AddDrDxtApplication() registers the P2 application layer:
//   * ISafetyManager           -> SafetyManager
//   * ITweakEngine             -> TweakEngine
//   * every ITweak (D/E/F)     -> registered so IEnumerable<ITweak> is non-empty
//   * ITweakCatalog            -> TweakCatalog (consumes IEnumerable<ITweak>)
//   * IStabilityTester         -> fail-clean stub (real harness later)
//   * the SDK seams            -> fail-clean stubs (real P/Invoke later)
//
// The seams are registered with TryAdd* so the App composition root can REPLACE
// any of them with the real implementation (NVAPI/ADLX/registry/power/etc.)
// without editing this file. The engine's own dependencies that belong to other
// modules (IDiagnosticsLog from Infrastructure, IBenchmarkRunner from Measurement)
// are expected to be registered by the host before this is called — this method
// does not register them so it stays a single-responsibility application module.
//
// SEAM STATUS (real vs stub): in M2 ALL SDK seams + IElevatedClient are fail-clean
// STUBS (see FailCleanSeamStubs). They report "unavailable" / refuse, so a tweak
// skips cleanly and nothing privileged happens by accident. The real signed-IPC
// IElevatedClient (DrDxt.ElevatedHelper) and the P/Invoke seams are wired in the
// App host, replacing these stubs.
// -----------------------------------------------------------------------
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddDrDxtApplication(this IServiceCollection services)
    {
        RegisterSeamStubs(services);
        RegisterTweakTargets(services);
        RegisterTweaks(services);

        services.TryAddSingleton<ITweakCatalog, TweakCatalog>();
        services.TryAddSingleton<ISafetyManager, SafetyManager>();
        services.TryAddSingleton<ITweakEngine, TweakEngine>();

        return services;
    }

    // --- Fail-clean SDK seam stubs (TryAdd: the App host overrides with real impls). ---
    private static void RegisterSeamStubs(IServiceCollection services)
    {
        // Privileged frontier: a refusing stub by default (no privileged write).
        services.TryAddSingleton<IElevatedClient, StubElevatedClient>();

        // Safety seams.
        services.TryAddSingleton<IRestorePointService, StubRestorePointService>();
        services.TryAddSingleton<IConsentPrompt, StubConsentPrompt>();

        // Latency seams.
        services.TryAddSingleton<INvApi, StubNvApi>();
        services.TryAddSingleton<IAdlx, StubAdlx>();
        services.TryAddSingleton<IDisplayInfo, StubDisplayInfo>();
        services.TryAddSingleton<IRegistryReader, StubRegistryReader>();

        // GPU-tuning seams.
        services.TryAddSingleton<INvApiInfo, StubNvApiInfo>();
        services.TryAddSingleton<INvApiGpuTuning, StubNvApiGpuTuning>();
        services.TryAddSingleton<IAdlxGpuTuning, StubAdlxGpuTuning>();
        // Real orchestration over an injected GPU-load probe (D3D11 probe lands next);
        // until the probe is real it reports CouldNotRun → Inconclusive → safe revert.
        services.TryAddSingleton<IGpuLoadProbe, StubGpuLoadProbe>();
        services.TryAddSingleton<IStabilityTester, GpuStabilityTester>();

        // Fix-it seams.
        services.TryAddSingleton<IPowerPlanReader, StubPowerPlanReader>();
        services.TryAddSingleton<ICpuPowerLimitReader, StubCpuPowerLimitReader>();
        services.TryAddSingleton<IServiceStateReader, StubServiceStateReader>();
        services.TryAddSingleton<IModeEcran, StubModeEcran>();
        services.TryAddSingleton<IConfirmationEcran, StubConfirmationEcran>();
    }

    // --- Default GPU tuning targets (value objects, not services). ---
    // Conservative, in-bounds defaults; in production these come from a per-chip
    // profile (M6). They are still re-clamped/gated by GpuSafeBounds before anything
    // reaches the helper — the target is a request, not an authorization.
    private static void RegisterTweakTargets(IServiceCollection services)
    {
        // A modest undervolt-direction offset, well inside GpuSafeBounds [-200, 100].
        services.TryAddSingleton(new GpuTuningTarget(GpuIndex: 0, CoreOffsetMhz: -100, MemoryOffsetMhz: 0));
        // A gentle power trim, inside [60, 100].
        services.TryAddSingleton(new GpuPowerLimitTarget(GpuIndex: 0, PowerLimitPct: 90));
        // M6 — the live, overridable target holder the GPU tweaks read at apply time,
        // seeded with the conservative defaults above. A per-chip profile Set()s it (after
        // clamping) at scan time; with no override it returns those defaults unchanged.
        services.TryAddSingleton<IGpuTuningTargetSource>(sp => new GpuTuningTargetSource(
            sp.GetRequiredService<GpuTuningTarget>(),
            sp.GetRequiredService<GpuPowerLimitTarget>()));
        // A sane cooling curve (every hot point >= the safe duty floor).
        services.TryAddSingleton(new GpuFanCurveTarget(GpuIndex: 0, new FanCurve(new[]
        {
            new FanCurvePoint(40, 30),
            new FanCurvePoint(60, 45),
            new FanCurvePoint(80, 65),
            new FanCurvePoint(90, 85),
        })));
    }

    // --- Every ITweak (D/E/F), so IEnumerable<ITweak> flows into the catalog. ---
    private static void RegisterTweaks(IServiceCollection services)
    {
        // D — Latency / OS-persistent (NS-1)
        services.AddSingleton<ITweak, NvidiaUltraLowLatencyTweak>();
        services.AddSingleton<ITweak, RenderQueueCapTweak>();
        services.AddSingleton<ITweak, VrrVsyncCapConfigTweak>();
        services.AddSingleton<ITweak, HagsOnTweak>();
        services.AddSingleton<ITweak, GameModeOnTweak>();
        services.AddSingleton<ITweak, AccelerationSourisOffTweak>();
        services.AddSingleton<ITweak, GameDvrBackgroundOffTweak>();
        services.AddSingleton<ITweak, VrrFlipModelOnTweak>();
        services.AddSingleton<ITweak, PreferMaxPerfTweak>();

        // E — GPU tuning
        services.AddSingleton<ITweak, GpuUndervoltTweak>();
        services.AddSingleton<ITweak, GpuPowerLimitTweak>();
        services.AddSingleton<ITweak, GpuFanCurveTweak>();

        // F — Fix-it
        services.AddSingleton<ITweak, X3dCcdParkingFixTweak>();
        services.AddSingleton<ITweak, SanePowerPlanTweak>();
        services.AddSingleton<ITweak, ModeBoostTweak>();
        services.AddSingleton<ITweak, LimitePuissanceCpuTweak>();
        services.AddSingleton<ITweak, SanePagefileTweak>();
        services.AddSingleton<ITweak, HeuresActivesTweak>();
        services.AddSingleton<ITweak, EcranFrequenceTweak>();
    }
}
