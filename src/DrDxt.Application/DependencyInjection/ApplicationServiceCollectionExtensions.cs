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
// Câblage DI de la couche Application : AddDrDxtApplication() enregistre
//   * ISafetyManager           -> SafetyManager
//   * ITweakEngine             -> TweakEngine
//   * chaque ITweak            -> enregistré pour que IEnumerable<ITweak> ne soit pas vide
//   * ITweakCatalog            -> TweakCatalog (consomme IEnumerable<ITweak>)
//   * IStabilityTester         -> GpuStabilityTester, sur une sonde de charge qui échoue proprement tant que l'App
//                                 n'enregistre pas la vraie
//   * les coutures SDK et IElevatedClient -> stubs qui échouent proprement (FailCleanSeamStubs) : rien de privilégié
//                                 n'arrive par accident
//
// Enregistrées par TryAdd* : l'App y substitue les vraies (IPC signé de DrDxt.ElevatedHelper, NVAPI, ADLX, registre…)
// sans toucher ce fichier. IDiagnosticsLog (Infrastructure) et IBenchmarkRunner (Measurement) sont enregistrés par
// l'hôte avant cet appel.
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

    // --- Stubs des coutures SDK qui échouent proprement (TryAdd : l'hôte App les remplace par les vraies). ---
    private static void RegisterSeamStubs(IServiceCollection services)
    {
        // Frontière privilégiée : par défaut un stub qui refuse (aucune écriture privilégiée).
        services.TryAddSingleton<IElevatedClient, StubElevatedClient>();

        // Coutures de sécurité.
        services.TryAddSingleton<IRestorePointService, StubRestorePointService>();
        services.TryAddSingleton<IConsentPrompt, StubConsentPrompt>();

        // Coutures de latence.
        services.TryAddSingleton<INvApi, StubNvApi>();
        services.TryAddSingleton<IAdlx, StubAdlx>();
        services.TryAddSingleton<IDisplayInfo, StubDisplayInfo>();
        services.TryAddSingleton<IRegistryReader, StubRegistryReader>();

        // Coutures de réglage GPU.
        services.TryAddSingleton<INvApiGpuTuning, StubNvApiGpuTuning>();
        services.TryAddSingleton<IAdlxGpuTuning, StubAdlxGpuTuning>();
        // La sonde de charge GPU (D3D11) vient de l'App ; le stub rend CouldNotRun → Inconclusive → retour sûr.
        services.TryAddSingleton<IGpuLoadProbe, StubGpuLoadProbe>();
        services.TryAddSingleton<IStabilityTester, GpuStabilityTester>();

        // Coutures des réparations.
        services.TryAddSingleton<IPowerPlanReader, StubPowerPlanReader>();
        services.TryAddSingleton<ICpuPowerLimitReader, StubCpuPowerLimitReader>();
        services.TryAddSingleton<IServiceStateReader, StubServiceStateReader>();
        services.TryAddSingleton<IModeEcran, StubModeEcran>();
        services.TryAddSingleton<IConfirmationEcran, StubConfirmationEcran>();
    }

    // --- Cibles de réglage GPU par défaut (objets valeur, pas des services). ---
    // Défauts prudents ; en production, un profil de puce. Rebornés par GpuSafeBounds avant le helper : une demande, pas
    // une autorisation.
    private static void RegisterTweakTargets(IServiceCollection services)
    {
        // Un décalage modeste dans le sens de la sous-tension, bien dans GpuSafeBounds [-200, 100].
        services.TryAddSingleton(new GpuTuningTarget(GpuIndex: 0, CoreOffsetMhz: -100, MemoryOffsetMhz: 0));
        // Une légère baisse de puissance, dans [60, 100].
        services.TryAddSingleton(new GpuPowerLimitTarget(GpuIndex: 0, PowerLimitPct: 90));
        // Le porteur de cible lu à la pose, amorcé de ces défauts ; un profil de puce le Set() (borné) au scan.
        services.TryAddSingleton<IGpuTuningTargetSource>(sp => new GpuTuningTargetSource(
            sp.GetRequiredService<GpuTuningTarget>(),
            sp.GetRequiredService<GpuPowerLimitTarget>()));
        // Une courbe de refroidissement saine (chaque point chaud >= le plancher de cycle sûr).
        services.TryAddSingleton(new GpuFanCurveTarget(GpuIndex: 0, new FanCurve(new[]
        {
            new FanCurvePoint(40, 30),
            new FanCurvePoint(60, 45),
            new FanCurvePoint(80, 65),
            new FanCurvePoint(90, 85),
        })));
    }

    // --- Chaque ITweak (D/E/F), pour que IEnumerable<ITweak> alimente le catalogue. ---
    private static void RegisterTweaks(IServiceCollection services)
    {
        // D : latence / persistant côté OS
        services.AddSingleton<ITweak, NvidiaUltraLowLatencyTweak>();
        services.AddSingleton<ITweak, RenderQueueCapTweak>();
        services.AddSingleton<ITweak, VrrVsyncCapConfigTweak>();
        services.AddSingleton<ITweak, HagsOnTweak>();
        services.AddSingleton<ITweak, GameModeOnTweak>();
        services.AddSingleton<ITweak, AccelerationSourisOffTweak>();
        services.AddSingleton<ITweak, GameDvrBackgroundOffTweak>();
        services.AddSingleton<ITweak, VrrFlipModelOnTweak>();
        services.AddSingleton<ITweak, PreferMaxPerfTweak>();

        // E : réglage GPU
        services.AddSingleton<ITweak, GpuUndervoltTweak>();
        services.AddSingleton<ITweak, GpuPowerLimitTweak>();
        services.AddSingleton<ITweak, GpuFanCurveTweak>();

        // F : réparations
        services.AddSingleton<ITweak, X3dCcdParkingFixTweak>();
        services.AddSingleton<ITweak, SanePowerPlanTweak>();
        services.AddSingleton<ITweak, ModeBoostTweak>();
        services.AddSingleton<ITweak, LimitePuissanceCpuTweak>();
        services.AddSingleton<ITweak, PlafondThermiqueCpuTweak>();
        services.AddSingleton<ITweak, EconomieSansFilTweak>();
        services.AddSingleton<ITweak, PlanEconomieTweak>();
        services.AddSingleton<ITweak, SanePagefileTweak>();
        services.AddSingleton<ITweak, PagefileSurSsdTweak>();
        services.AddSingleton<ITweak, HeuresActivesTweak>();
        services.AddSingleton<ITweak, EcranFrequenceTweak>();
    }
}
