using DrDxt.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Bios;

/// <summary>
/// DI wiring for the BIOS Doctor module: the read+diff advisor, the data-driven guide
/// library (loaded + validated from embedded data at first resolution), and the guide
/// generator. All singletons — the library is immutable validated data and the
/// generator is stateless. Registered with TryAdd so a host can substitute a fake.
/// </summary>
public static class BiosServiceCollectionExtensions
{
    public static IServiceCollection AddDrDxtBios(this IServiceCollection services)
    {
        services.TryAddSingleton<IBiosAdvisor, BiosAdvisor>();

        // The library validates the embedded data at construction; a packaging fault
        // surfaces loudly here (a bug we want loud), not as a silent empty guide.
        services.TryAddSingleton<IGuideLibrary>(_ => GuideLibraryFactory.CreateDefault());

        services.TryAddSingleton<IGuideGenerator>(sp =>
            new GuideGenerator(sp.GetRequiredService<IGuideLibrary>()));

        // ── Closed-loop verification (M3): the injectable clock, the file-backed
        //    pending-change marker store, and the post-reboot verifier. The store
        //    writes under %LOCALAPPDATA%\DrDXT\ via the real filesystem seam. ──
        services.TryAddSingleton<IBiosClock, SystemBiosClock>();
        services.TryAddSingleton<IBiosMachineId, SystemBiosMachineId>();
        services.TryAddSingleton<IPendingBiosChangeStore>(_ => new PendingBiosChangeStore());
        services.TryAddSingleton<IBiosLoopVerifier>(sp =>
            new BiosLoopVerifier(
                sp.GetRequiredService<IPendingBiosChangeStore>(),
                sp.GetRequiredService<IBiosMachineId>()));

        return services;
    }
}
