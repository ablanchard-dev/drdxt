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
        // SettingsStateReader prend INvApi, IDisplayInfo et le lecteur d'arrets brutaux en parametres optionnels : non
        // enregistres, null = jamais lu (DetectionDiTests). Le lecteur d'ecrans est ici pour tout ce qui monte ce conteneur
        // (sinon `AttachedRefreshHz = null`). Fabrique explicite avec NVAPI : par type, il prendrait le constructeur sans
        // NVAPI et dirait G-Sync « non lisible ». FreeSync demande IAdlxNative, enregistre par l'hote. TryAdd : l'App passe
        // en premier si elle s'enregistre avant.
        services.TryAddSingleton<DrDxt.GpuNative.NvApi.INvApiNative>(_ => new DrDxt.GpuNative.NvApi.NvApiNative());
        services.TryAddSingleton<DrDxt.Application.Tweaks.Hardware.IDisplayInfo>(sp => new Display.EcransEnCache(
            new Display.Win32DisplayInfo(sp.GetService<DrDxt.GpuNative.NvApi.INvApiNative>(),
                sp.GetService<DrDxt.Detection.Gpu.Adlx.IAdlxNative>())));

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
