using Serilog;
using Serilog.Core;

namespace DrDxt.Infrastructure.Logging;

/// <summary>
/// Amorçage Serilog minimal. Aucune modification statique globale : les appelants possèdent la durée de vie.
/// </summary>
public static class AppLogging
{
    /// <summary>
    /// Crée un <see cref="ILogger"/> Serilog qui écrit des fichiers quotidiens tournants dans
    /// <paramref name="logDirectory"/>. L'appelant libère le logger rendu à l'arrêt de l'application.
    /// </summary>
    /// <param name="logDirectory">
    /// Dossier des fichiers de journal. Par défaut <c>%LOCALAPPDATA%\DrDXT\logs</c>.
    /// Injecter un dossier temporaire en test pour ne rien salir.
    /// </param>
    public static ILogger CreateLogger(string? logDirectory = null)
    {
        logDirectory ??= DrDxt.Core.DossierDonnees.Chemin("logs");

        Directory.CreateDirectory(logDirectory);

        var logFile = Path.Combine(logDirectory, "drdxt-.log");

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,   // 10 MB per file
                retainedFileCountLimit: 7,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(2))
            .CreateLogger();
    }
}
