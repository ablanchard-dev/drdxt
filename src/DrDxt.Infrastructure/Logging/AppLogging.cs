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
    /// <param name="eleve">Vrai si l'App tourne élevée ; par défaut, le jeton du processus. Couture de test.</param>
    public static ILogger CreateLogger(string? logDirectory = null, bool? eleve = null)
    {
        // Le dossier des journaux appartient au joueur : le nom du journal du lendemain peut y être posé d'avance en lien, et
        // le puits fichier de Serilog l'ouvrirait en administrateur. Élevée, l'App n'écrit aucun journal fichier.
        // Plafond : le journal détaillé est perdu en mode élevé (restent le journal des changements et crash.log, écrits
        // sans suivre de lien) ; si le support en a besoin, un dossier sous ProgramData durci par EnsureCreatedHardened.
        if (eleve ?? ProcessusEleve())
            return new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();

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

    private static bool ProcessusEleve()
    {
        using var identite = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identite).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
