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
// Câblage DI de la couche Application.
//
// AddDrDxtApplication() enregistre la couche Application :
//   * ISafetyManager           -> SafetyManager
//   * ITweakEngine             -> TweakEngine
//   * chaque ITweak (D/E/F)    -> enregistré pour que IEnumerable<ITweak> ne soit pas vide
//   * ITweakCatalog            -> TweakCatalog (consomme IEnumerable<ITweak>)
//   * IStabilityTester         -> stub qui échoue proprement (vrai banc plus tard)
//   * les coutures SDK         -> stubs qui échouent proprement (vrai P/Invoke plus tard)
//
// Les coutures sont enregistrées par TryAdd* : la racine de composition de l'App peut
// remplacer chacune par la vraie implémentation (NVAPI/ADLX/registre/alimentation/etc.)
// sans toucher ce fichier. Les dépendances du moteur qui appartiennent à d'autres modules
// (IDiagnosticsLog d'Infrastructure, IBenchmarkRunner de Measurement) doivent être
// enregistrées par l'hôte avant cet appel ; cette méthode ne les enregistre pas, pour rester
// un module d'application à une seule responsabilité.
//
// État des coutures (vraies ou stubs) : au départ toutes les coutures SDK + IElevatedClient
// sont des stubs qui échouent proprement (voir FailCleanSeamStubs). Ils disent « indisponible »
// ou refusent : un levier passe proprement et rien de privilégié n'arrive par accident. Le
// vrai IElevatedClient en IPC signé (DrDxt.ElevatedHelper) et les coutures P/Invoke sont
// branchés dans l'hôte App, à la place de ces stubs.
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
        // Vraie orchestration sur une sonde de charge GPU injectée (la sonde D3D11 vient de l'App) ;
        // tant que la sonde n'est pas réelle, elle rend CouldNotRun → Inconclusive → retour sûr.
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
    // Défauts prudents, dans les bornes ; en production, ils viennent d'un profil de puce.
    // Ils sont quand même rebornés et filtrés par GpuSafeBounds avant d'atteindre le helper :
    // la cible est une demande, pas une autorisation.
    private static void RegisterTweakTargets(IServiceCollection services)
    {
        // Un décalage modeste dans le sens de la sous-tension, bien dans GpuSafeBounds [-200, 100].
        services.TryAddSingleton(new GpuTuningTarget(GpuIndex: 0, CoreOffsetMhz: -100, MemoryOffsetMhz: 0));
        // Une légère baisse de puissance, dans [60, 100].
        services.TryAddSingleton(new GpuPowerLimitTarget(GpuIndex: 0, PowerLimitPct: 90));
        // Le porteur de cible en cours, remplaçable, que les leviers GPU lisent à la pose,
        // amorcé avec les défauts prudents ci-dessus. Un profil de puce le Set() (après bornage)
        // au scan ; sans remplacement, il rend ces défauts tels quels.
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
