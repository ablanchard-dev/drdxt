using DrDxt.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Profiles;

// -----------------------------------------------------------------------
// M6 — DI wiring (mirrors AddDrDxtDetection / AddDrDxtApplication).
//
// AddDrDxtProfiles() registers the per-chip profile layer:
//   * IChipProfileCatalog  -> ChipProfileCatalog        (the sourced profiles)
//   * IChipProfileMatcher  -> ChipProfileMatcher        (detected chip → profile(s))
//   * IProfileMatcher      -> CoreProfileMatcherAdapter (Core contract, projects down)
//
// TryAdd* so a host/test composition can replace any of them. Pure registration — no
// scan/apply behaviour is rewired here; turning a profile's parameters into the M2
// GpuTuningTarget / building a TweakPlan from IProfileMatcher.Match is the consumption
// seam left to the App scan flow (documented, not forced).
// -----------------------------------------------------------------------
public static class ProfilesServiceCollectionExtensions
{
    public static IServiceCollection AddDrDxtProfiles(this IServiceCollection services)
    {
        services.TryAddSingleton<IChipProfileCatalog, ChipProfileCatalog>();
        services.TryAddSingleton<IChipProfileMatcher, ChipProfileMatcher>();
        services.TryAddSingleton<IProfileMatcher, CoreProfileMatcherAdapter>();
        return services;
    }
}
