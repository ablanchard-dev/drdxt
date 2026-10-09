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
        // Infrastructure providers (stateless, singleton for efficiency)
        services.TryAddSingleton<IWmiQuery>(_ => new RetryingWmiQuery(new ManagementWmiQuery()));
        services.TryAddSingleton<IDxgiProvider, VorticeDxgiProvider>();
        services.TryAddSingleton<IAdlxProvider, AdlxProvider>();
        services.TryAddSingleton<INvmlNative, NvmlNative>();
        services.TryAddSingleton<INvReBarProvider, NvmlReBarProvider>();
        services.TryAddSingleton<IFirmwareProvider, FirmwareProvider>();
        services.TryAddSingleton<IPowerProvider, PowerProvider>();
        services.TryAddSingleton<IRegistryProvider, RegistryProvider>();

        // Concrete detectors — registered so DI can build HardwareDetector
        services.TryAddSingleton<CpuDetector>();
        services.TryAddSingleton<GpuDetector>();
        services.TryAddSingleton<MemoryDetector>();
        services.TryAddSingleton<BoardBiosDetector>();
        services.TryAddSingleton<ChassisDetector>();

        // Composed services
        services.TryAddSingleton<IHardwareDetector, HardwareDetector>();
        // Cablage par convention : SettingsStateReader porte INvApi et IDisplayInfo en
        // parametres OPTIONNELS, et l'injection .NET honore leur valeur par defaut quand ils
        // ne sont pas enregistres. Verifie par mutation, pas suppose : les deux tests de
        // DetectionDiTests restent verts avec ce cablage-ci et avec une fabrique explicite,
        // donc la fabrique etait du code en plus sans garantie en plus.
        // LR — le lecteur d'arrets brutaux, injecte dans le parametre optionnel de
        // SettingsStateReader par la meme convention que INvApi et IDisplayInfo. Non
        // enregistre = champ null = jamais lu, ce qui reste une reponse honnete.
        // Le lecteur d'ecrans appartient ici, et non a la seule couche app. Tant qu'il n'etait
        // enregistre que par l'application WPF, tout ce qui monte ce conteneur -- les sondes, les
        // tests d'integration, un futur mode console -- lisait `AttachedRefreshHz = null` et se
        // taisait sur la consommation au repos. Le diagnostic naissait donc mort partout sauf dans
        // une fenetre. `TryAdd` : l'enregistrement explicite de l'App reste prioritaire s'il passe
        // en premier, et rien ne change pour elle.
        // L8 suite (10/09/2026) — G-Sync lu au pilote (NVAPI, lecture non elevee). Enregistre par TYPE,
        // le lecteur prenait son constructeur sans NVAPI : le rapport disait « VRR non lisible » alors
        // que le pilote repondait. Fabrique explicite, TryAdd : l'App garde la main.
        services.TryAddSingleton<DrDxt.GpuNative.NvApi.INvApiNative>(_ => new DrDxt.GpuNative.NvApi.NvApiNative());
        services.TryAddSingleton<DrDxt.Application.Tweaks.Hardware.IDisplayInfo>(sp =>
            new Display.Win32DisplayInfo(sp.GetService<DrDxt.GpuNative.NvApi.INvApiNative>(),
                sp.GetService<DrDxt.Detection.Gpu.Adlx.IAdlxNative>()));

        services.TryAddSingleton<IUnexpectedShutdownLog, WindowsUnexpectedShutdownLog>();
        // PHASE 18 F2 — HAGS effectif (D3DKMT), meme convention de parametre optionnel.
        services.TryAddSingleton<IHagsEffectiveReader, D3dkmtHagsReader>();
        services.TryAddSingleton<ISettingsStateReader, SettingsStateReader>();
        services.TryAddSingleton<IScanScorer, ScanScorer>();
        services.TryAddSingleton<IStorageDetector, StorageDetector>();
        // NS-9 — network adapter read (guidance only; there is no network apply path).
        services.TryAddSingleton<INetworkAdapterReader, NetworkAdapterReader>();

        return services;
    }
}
