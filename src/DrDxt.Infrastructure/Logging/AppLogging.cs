using Serilog;
using Serilog.Core;

namespace DrDxt.Infrastructure.Logging;

/// <summary>
/// Minimal Serilog bootstrap. No global static mutation — callers own the lifetime.
/// </summary>
public static class AppLogging
{
    /// <summary>
    /// Creates a Serilog <see cref="ILogger"/> that writes rolling daily files to
    /// <paramref name="logDirectory"/>. The caller is responsible for disposing the
    /// returned logger when the application shuts down.
    /// </summary>
    /// <param name="logDirectory">
    /// Directory for log files. Defaults to <c>%LOCALAPPDATA%\DrDXT\logs</c>.
    /// Inject a temp directory in tests to avoid pollution.
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
