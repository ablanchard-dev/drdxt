using DrDxt.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Profiles;

// -----------------------------------------------------------------------
// Câblage de l'injection (comme AddDrDxtDetection et AddDrDxtApplication).
//
// AddDrDxtProfiles() enregistre la couche des profils par puce :
//   * IChipProfileCatalog  -> ChipProfileCatalog        (les profils sourcés)
//   * IChipProfileMatcher  -> ChipProfileMatcher        (puce détectée → profils)
//   * IProfileMatcher      -> CoreProfileMatcherAdapter (contrat de Core, projection)
//
// TryAdd* pour qu'un hôte ou un test puisse remplacer chacun. Enregistrement pur : rien du
// scan ni de la pose n'est recâblé ici ; transformer les paramètres d'un profil en
// GpuTuningTarget ou bâtir un TweakPlan depuis IProfileMatcher.Match reste la couture de
// consommation du flux de scan de l'App (documentée, pas imposée).
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
