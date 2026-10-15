using DrDxt.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Bios;

/// <summary>
/// Branchement DI du module BIOS Doctor : le conseiller lecture + écart, la bibliothèque de guides
/// pilotée par les données (chargée et validée depuis les données embarquées à la première
/// résolution) et le générateur de guides. Tous singletons : la bibliothèque est une donnée validée
/// immuable et le générateur est sans état. Enregistrés avec TryAdd pour qu'un hôte y substitue un faux.
/// </summary>
public static class BiosServiceCollectionExtensions
{
    public static IServiceCollection AddDrDxtBios(this IServiceCollection services)
    {
        services.TryAddSingleton<IBiosAdvisor, BiosAdvisor>();

        // La bibliothèque valide les données embarquées à la construction ; une faute d'empaquetage
        // remonte bruyamment ici (un bug qu'on veut bruyant), pas en guide vide silencieux.
        services.TryAddSingleton<IGuideLibrary>(_ => GuideLibraryFactory.CreateDefault());

        services.TryAddSingleton<IGuideGenerator>(sp =>
            new GuideGenerator(sp.GetRequiredService<IGuideLibrary>()));

        // ── Vérification en boucle fermée : l'horloge injectable, le store de marqueur de changement en
        //    attente adossé à un fichier, et le vérificateur après redémarrage. Le store écrit sous
        //    %LOCALAPPDATA%\DrDXT\ par la vraie couture de système de fichiers. ──
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
