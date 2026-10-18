using DrDxt.Core;
using DrDxt.Detection.Gpu;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Network;
using DrDxt.Detection.Scoring;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;
using DrDxt.GpuNative.Nvml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Detection;

public static class DetectionServiceCollectionExtensions
{
    public static IServiceCollection AddDrDxtDetection(this IServiceCollection services)
    {
        // Fournisseurs d'infrastructure (sans état, singletons pour l'efficacité)
        services.TryAddSingleton<IWmiQuery>(_ => new RetryingWmiQuery(new ManagementWmiQuery()));
        services.TryAddSingleton<IDxgiProvider, VorticeDxgiProvider>();
        services.TryAddSingleton<IAdlxProvider, AdlxProvider>();
        services.TryAddSingleton<INvmlNative, NvmlNative>();
        services.TryAddSingleton<INvReBarProvider, NvmlReBarProvider>();
        services.TryAddSingleton<IFirmwareProvider, FirmwareProvider>();
        services.TryAddSingleton<IPowerProvider, PowerProvider>();
        services.TryAddSingleton<IRegistryProvider, RegistryProvider>();

        // Détecteurs concrets, enregistrés pour que la DI puisse construire HardwareDetector
        services.TryAddSingleton<CpuDetector>();
        services.TryAddSingleton<GpuDetector>();
        services.TryAddSingleton<MemoryDetector>();
        services.TryAddSingleton<BoardBiosDetector>();
        services.TryAddSingleton<ChassisDetector>();

        // Composed services
        services.TryAddSingleton<IHardwareDetector, HardwareDetector>();
        // Cablage par convention : SettingsStateReader porte INvApi et IDisplayInfo en
        // parametres optionnels, et l'injection .NET honore leur valeur par defaut quand ils
        // ne sont pas enregistres. Verifie par mutation, pas suppose : les deux tests de
        // DetectionDiTests restent verts avec ce cablage-ci et avec une fabrique explicite,
        // donc la fabrique etait du code en plus sans garantie en plus.
        // LR : le lecteur d'arrets brutaux, injecte dans le parametre optionnel de
        // SettingsStateReader par la meme convention que INvApi et IDisplayInfo. Non
        // enregistre = champ null = jamais lu, ce qui reste une reponse honnete.
        // Le lecteur d'ecrans appartient ici, et non a la seule couche app : sinon tout ce qui monte
        // ce conteneur (sondes, tests d'integration, un futur mode console) lirait
        // `AttachedRefreshHz = null` et se tairait sur la consommation au repos. `TryAdd` :
        // l'enregistrement explicite de l'App reste prioritaire s'il passe en premier.
        // G-Sync lu au pilote (NVAPI, lecture non elevee). Enregistre par type, le lecteur prendrait
        // son constructeur sans NVAPI et dirait « VRR non lisible » alors que le pilote repond.
        // Fabrique explicite, TryAdd : l'App garde la main.
        services.TryAddSingleton<DrDxt.GpuNative.NvApi.INvApiNative>(_ => new DrDxt.GpuNative.NvApi.NvApiNative());
        services.TryAddSingleton<DrDxt.Application.Tweaks.Hardware.IDisplayInfo>(sp =>
            new Display.Win32DisplayInfo(sp.GetService<DrDxt.GpuNative.NvApi.INvApiNative>(),
                sp.GetService<DrDxt.Detection.Gpu.Adlx.IAdlxNative>()));

        services.TryAddSingleton<IUnexpectedShutdownLog, WindowsUnexpectedShutdownLog>();
        // Les vidages d'écran bleu : la garde XMP de SettingsStateReader lit la même stabilité que Curve Optimizer.
        services.TryAddSingleton<IDumpCreationLog, WindowsDumpCreationLog>();
        // HAGS effectif (D3DKMT), meme convention de parametre optionnel.
        services.TryAddSingleton<IHagsEffectiveReader, D3dkmtHagsReader>();
        services.TryAddSingleton<ISettingsStateReader, SettingsStateReader>();
        services.TryAddSingleton<IScanScorer, ScanScorer>();
        services.TryAddSingleton<IStorageDetector, StorageDetector>();
        // Lecture des cartes réseau (guidage seul ; il n'y a aucun chemin d'application réseau).
        services.TryAddSingleton<INetworkAdapterReader, NetworkAdapterReader>();

        return services;
    }
}
