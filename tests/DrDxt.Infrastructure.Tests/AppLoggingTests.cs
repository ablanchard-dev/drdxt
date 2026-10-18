using DrDxt.Infrastructure.Logging;
using FluentAssertions;
using Serilog.Core;

namespace DrDxt.Infrastructure.Tests;

// Le dossier des journaux appartient au joueur : le nom du journal du lendemain peut y être posé d'avance en lien, et le
// puits fichier de Serilog l'ouvrirait. Élevée, l'App n'écrit donc aucun journal fichier.
public sealed class AppLoggingTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* dossier temporaire du test */ }
    }

    private void Journaliser(bool eleve)
    {
        var logger = AppLogging.CreateLogger(_dossier, eleve);
        logger.Information("ligne");
        (logger as Logger)!.Dispose();
    }

    [Fact]
    public void Elevee_l_App_n_ecrit_aucun_journal_fichier()
    {
        Journaliser(eleve: true);

        (Directory.Exists(_dossier) ? Directory.GetFiles(_dossier) : []).Should().BeEmpty();
    }

    [Fact]
    public void Sans_elevation_le_journal_fichier_s_ecrit()
    {
        Journaliser(eleve: false);

        Directory.GetFiles(_dossier, "drdxt-*.log").Should().HaveCount(1);
    }
}
