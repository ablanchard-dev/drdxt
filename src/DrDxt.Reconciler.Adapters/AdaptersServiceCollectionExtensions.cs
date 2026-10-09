using DrDxt.Reconciler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Reconciler.Adapters;

// -----------------------------------------------------------------------
// DI wiring for the production reconciler probes (OS-coupled adapters).
//
// Registers the 3 prod probes + the descriptor catalog seam + the re-apply mapper /
// client against their pure DrDxt.Reconciler interfaces. The Host (verify-only
// reconciler service) and the App call this; this bundle does not wire App/Host yet
// (a later increment).
//
// PREREQUISITES (Host/App supply these, like Detection's IWmiQuery):
//   * WmiGpuDriverProbe depends on Detection's IWmiQuery — register AddDrDxtDetection,
//     or a bare IWmiQuery → ManagementWmiQuery.
//   * ElevatedReapplyClient depends on the elevated IPC IElevatedClient — the Host
//     wires the real helper client; until then it stays unresolved by design.
// We use TryAdd so a caller that already registered these wins. The mapper and client
// are pure types (DrDxt.Reconciler), registered here because the descriptor catalog
// they bind to is also wired here — keeping the read/write location table in one place.
// -----------------------------------------------------------------------
public static class AdaptersServiceCollectionExtensions
{
    public static IServiceCollection AddDrDxtReconcilerAdapters(this IServiceCollection services)
    {
        // The single-source-of-truth descriptor table (pure data).
        services.TryAddSingleton<IReconcilableTweakCatalog, DefaultReconcilableTweakCatalog>();

        // The three production probes (stateless → singletons).
        services.TryAddSingleton<IOsBuildProbe, RegistryOsBuildProbe>();
        services.TryAddSingleton<IGpuDriverProbe, WmiGpuDriverProbe>();
        services.TryAddSingleton<ISettingProbe, SurfaceSettingProbe>();

        // The re-apply security boundary: the mapper (tweakId → existing typed op,
        // bound to the same descriptor catalog above) and the client that dispatches
        // it through the elevated IPC. Stateless → singletons. IElevatedClient is a
        // Host/App prerequisite (see header).
        services.TryAddSingleton<IReapplyOpMapper, DefaultReapplyOpMapper>();
        services.TryAddSingleton<IElevatedReapplyClient, ElevatedReapplyClient>();

        return services;
    }
}
