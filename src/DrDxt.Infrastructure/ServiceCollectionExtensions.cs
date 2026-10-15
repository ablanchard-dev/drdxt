using DrDxt.Core;
using DrDxt.Infrastructure.Diagnostics;
using DrDxt.Infrastructure.Localization;
using DrDxt.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Infrastructure;

public static class ServiceCollectionExtensions
{
    // Feature modules add AddDrDxt<Module>() alongside their implementations.

    public static IServiceCollection AddDrDxtCore(
        this IServiceCollection services,
        string? diagnosticsDirectory = null,
        string? logDirectory = null)
    {
        string diagDir = diagnosticsDirectory
            ?? DrDxt.Core.DossierDonnees.Chemin("diagnostics");

        // Le logger et son dossier de journaux sont créés paresseusement à la première résolution.
        // Note pour l'hôte App : libérer le ServiceProvider (ou appeler Serilog Log.CloseAndFlush) à la
        // sortie de l'application pour que le récepteur fichier vide ses dernières écritures en tampon.
        // Le conteneur libère ce singleton créé par fabrique parce que le Serilog.Core.Logger concret est
        // IDisposable, mais seulement si le fournisseur lui-même est libéré à l'arrêt.
        services.TryAddSingleton<Serilog.ILogger>(_ => AppLogging.CreateLogger(logDirectory));

        services.TryAddSingleton<IDiagnosticsLog>(sp =>
        {
            var logger = sp.GetRequiredService<Serilog.ILogger>();
            return new DiagnosticsLog(diagDir, logger);
        });

        // Localisation bilingue : anglais neutre (embarqué) + assemblage satellite FR.
        services.TryAddSingleton<ILocalizer, ResxLocalizer>();

        return services;
    }
}
