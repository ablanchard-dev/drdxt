using DrDxt.Core;
using DrDxt.Infrastructure.Diagnostics;
using DrDxt.Infrastructure.Localization;
using DrDxt.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Infrastructure;

public static class ServiceCollectionExtensions
{
    // Feature modules add AddDrDxt<Module>() alongside their implementations (M1+).

    public static IServiceCollection AddDrDxtCore(
        this IServiceCollection services,
        string? diagnosticsDirectory = null,
        string? logDirectory = null)
    {
        string diagDir = diagnosticsDirectory
            ?? DrDxt.Core.DossierDonnees.Chemin("diagnostics");

        // The logger and its log directory are created lazily on first resolution.
        // NOTE for the App host (M1+): dispose the ServiceProvider (or call
        // Serilog Log.CloseAndFlush) on application exit so the file sink flushes
        // its final buffered writes. The container disposes this factory-created
        // singleton because the concrete Serilog.Core.Logger is IDisposable, but
        // only if the provider itself is disposed at shutdown.
        services.TryAddSingleton<Serilog.ILogger>(_ => AppLogging.CreateLogger(logDirectory));

        services.TryAddSingleton<IDiagnosticsLog>(sp =>
        {
            var logger = sp.GetRequiredService<Serilog.ILogger>();
            return new DiagnosticsLog(diagDir, logger);
        });

        // Bilingual localization — en neutral (embedded) + FR satellite assembly.
        services.TryAddSingleton<ILocalizer, ResxLocalizer>();

        return services;
    }
}
