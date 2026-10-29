using DrDxt.Reconciler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Reconciler.Adapters;

// -----------------------------------------------------------------------
// Branchement DI des adaptateurs OS du réconciliateur : les 3 sondes de production, le catalogue de descripteurs, le
// mappeur et le client de réapplication, sur les interfaces de DrDxt.Reconciler ; appelé par l'hôte et l'App.
// Prérequis fournis par eux :
//   * WmiGpuDriverProbe dépend de l'IWmiQuery de Detection : enregistrer AddDrDxtDetection, ou
//     un IWmiQuery nu → ManagementWmiQuery.
//   * ElevatedReapplyClient dépend de l'IElevatedClient de l'IPC élevé : l'hôte branche le vrai
//     client du helper ; sans lui, il reste non résolu, par conception.
// TryAdd : un enregistrement antérieur l'emporte. Mappeur et client (purs) sont ici avec le catalogue auquel ils se
// lient : la table des emplacements reste en un seul endroit.
// -----------------------------------------------------------------------
public static class AdaptersServiceCollectionExtensions
{
    public static IServiceCollection AddDrDxtReconcilerAdapters(this IServiceCollection services)
    {
        // La table des descripteurs, source unique (données pures).
        services.TryAddSingleton<IReconcilableTweakCatalog, DefaultReconcilableTweakCatalog>();

        // Les trois sondes de production (sans état → singletons).
        services.TryAddSingleton<IOsBuildProbe, RegistryOsBuildProbe>();
        services.TryAddSingleton<IGpuDriverProbe, WmiGpuDriverProbe>();
        services.TryAddSingleton<ISettingProbe, SurfaceSettingProbe>();

        // La frontière de sécurité de la réapplication : le mappeur (tweakId → op typée, même catalogue) et le client par
        // l'IPC élevé, sans état. IElevatedClient vient de l'hôte ou de l'App.
        services.TryAddSingleton<IReapplyOpMapper, DefaultReapplyOpMapper>();
        services.TryAddSingleton<IElevatedReapplyClient, ElevatedReapplyClient>();

        return services;
    }
}
