using DrDxt.Reconciler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Reconciler.Adapters;

// -----------------------------------------------------------------------
// Branchement DI des sondes de production du réconciliateur (adaptateurs liés à l'OS).
//
// Enregistre les 3 sondes de production + la couture du catalogue de descripteurs + le mappeur /
// client de réapplication sur leurs interfaces pures de DrDxt.Reconciler. L'hôte (service
// réconciliateur en vérification seule) et l'App l'appellent ; ce lot ne branche pas lui-même
// l'App ni l'hôte.
//
// Prérequis (fournis par l'hôte / l'App, comme l'IWmiQuery de Detection) :
//   * WmiGpuDriverProbe dépend de l'IWmiQuery de Detection : enregistrer AddDrDxtDetection, ou
//     un IWmiQuery nu → ManagementWmiQuery.
//   * ElevatedReapplyClient dépend de l'IElevatedClient de l'IPC élevé : l'hôte branche le vrai
//     client du helper ; sans lui, il reste non résolu, par conception.
// TryAdd : un appelant qui les a déjà enregistrés l'emporte. Le mappeur et le client sont des
// types purs (DrDxt.Reconciler), enregistrés ici parce que le catalogue de descripteurs auquel
// ils se lient est branché ici aussi : la table des emplacements de lecture / écriture reste en
// un seul endroit.
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

        // La frontière de sécurité de la réapplication : le mappeur (tweakId → op typée existante, lié au
        // même catalogue de descripteurs ci-dessus) et le client qui l'envoie par l'IPC élevé. Sans état
        // → singletons. IElevatedClient est un prérequis de l'hôte / de l'App (voir l'en-tête).
        services.TryAddSingleton<IReapplyOpMapper, DefaultReapplyOpMapper>();
        services.TryAddSingleton<IElevatedReapplyClient, ElevatedReapplyClient>();

        return services;
    }
}
