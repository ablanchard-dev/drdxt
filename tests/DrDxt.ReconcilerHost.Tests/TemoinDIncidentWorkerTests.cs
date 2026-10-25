using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// Notre règle pour le témoin d'incident : du code compilé du service, dans un dossier dont DrDXT fixe les droits. Jamais un
// script lancé par une tâche SYSTEM dans un dossier que le compte pourrait modifier.
public sealed class TemoinDIncidentWorkerTests
{
    private static string Source()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.ReconcilerHost", "TemoinDIncidentWorker.cs"));
    }

    [Fact]
    public void Le_temoin_s_ecrit_dans_un_dossier_durci_par_le_service_lui_meme()
    {
        var s = Source();
        s.Should().Contain("TrustedManifestRoot.EnsureCreatedHardened(_dossier)").And.Contain("TrustedManifestRoot.WriteTrustedFile(");
        s.Should().NotContain("File.WriteAll");
    }

    // %ProgramData% laisse tout compte y créer un dossier : un « DrDxt » posé avant le service, ou une jonction, mettrait
    // les témoins (et leurs suppressions) ailleurs. Le parent est durci d'abord, point de reparse refusé.
    [Fact]
    public void Le_parent_des_temoins_est_durci_avant_eux()
    {
        var s = Source();
        var parent = s.IndexOf("TrustedManifestRoot.EnsureCreatedHardened(Path.GetDirectoryName(_dossier)!)", StringComparison.Ordinal);
        parent.Should().BeGreaterThan(0);
        parent.Should().BeLessThan(s.IndexOf("TrustedManifestRoot.EnsureCreatedHardened(_dossier)", StringComparison.Ordinal));
    }

    // Les témoins posés d'avance par un compte (la réparation du dossier ne descend pas) : ni relus, ni comptés, ni supprimés.
    [Fact]
    public void Le_service_ne_relit_et_ne_purge_que_des_temoins_de_confiance()
    {
        var s = Source();
        s.Should().Contain("TemoinDIncident.LireTous(_dossier, DeConfiance)").And.Contain("TemoinDIncident.EnTrop(_dossier, DeConfiance)")
            .And.Contain("TrustedManifestRoot.EnsureTrustedFile(fichier).IsOk");
        s.Should().NotContain("Directory.EnumerateFiles(");
    }

    // Le rappel du journal tourne sur un fil du pool : une exception y arrêterait tout le service, réconciliateur compris.
    [Fact]
    public void Un_temoin_qui_echoue_ne_fait_jamais_tomber_le_service()
    {
        var w = new TemoinDIncidentWorker(Path.Combine(Path.GetTempPath(), $"temoins-{Guid.NewGuid():N}"),
            (_, _) => throw new InvalidOperationException("journal en panne"));
        var appel = () => w.Temoigner(DateTimeOffset.Now, "arret brutal");
        appel.Should().NotThrow();
    }

    [Theory]
    [InlineData("ScheduledTask")]
    [InlineData("schtasks")]
    [InlineData(".ps1")]
    [InlineData("Process.Start")]
    [InlineData("powershell")]
    public void Le_temoin_ne_pose_ni_tache_ni_script(string motif)
        => Source().Should().NotContainEquivalentOf(motif);

    [Fact]
    public void Le_dossier_est_sous_les_donnees_communes_de_DrDXT()
        => TemoinDIncidentWorker.DossierParDefaut().Should().EndWith(Path.Combine("DrDxt", "temoins"));

    // Un dernier témoin daté dans le futur (horloge partie en avant puis corrigée) ferait lire le journal à partir d'un
    // instant pas encore venu : rien ne serait rattrapé.
    [Fact]
    public void Le_rattrapage_part_du_dernier_temoin_sauf_s_il_est_dans_le_futur()
    {
        var maintenant = new DateTimeOffset(2026, 10, 23, 22, 0, 0, TimeSpan.Zero);

        TemoinDIncidentWorker.DebutDuRattrapage(maintenant.AddHours(-3), maintenant).Should().Be(maintenant.AddHours(-3));
        TemoinDIncidentWorker.DebutDuRattrapage(null, maintenant).Should().Be(maintenant.AddDays(-7));
        TemoinDIncidentWorker.DebutDuRattrapage(maintenant.AddDays(30), maintenant).Should().Be(maintenant.AddDays(-7));
    }
}
