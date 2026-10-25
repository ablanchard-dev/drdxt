using System.IO;
using DrDxt.App.Updates;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// L'installeur posé dans le staging (payload.pkg et DrDXT-Setup-&lt;v&gt;.exe, des dizaines de Mo) restait après une
/// installation réussie. Au lancement suivant, un installeur d'une version déjà installée (ou plus ancienne) a servi : le
/// dossier part. Une version plus récente, pas encore installée, reste pour le clic.
/// </summary>
public sealed class NettoyageDuStagingTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-staging-" + Guid.NewGuid().ToString("N"), "app-staging");
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_dossier)!, true); } catch (IOException) { } }

    private void Poser(string version)
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Path.Combine(_dossier, "payload.pkg"), "charge");
        File.WriteAllText(Path.Combine(_dossier, $"DrDXT-Setup-{version}.exe"), "installeur");
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.1.0", "1.2.0", true)]
    [InlineData("1.3.0", "1.2.0", false)]
    public void Un_installeur_deja_installe_part_au_lancement(string posee, string installee, bool purge)
    {
        Poser(posee);

        NettoyageDuStaging.Purger(_dossier, new Version(installee));

        Directory.Exists(_dossier).Should().Be(!purge);
    }

    // Le dossier purgé est celui où l'updater pose l'installeur, pas une copie du chemin qui dériverait.
    [Fact]
    public void Le_dossier_purge_est_celui_de_l_updater()
    {
        var racine = Path.GetDirectoryName(Path.GetDirectoryName(_dossier))!;
        using var client = new System.Net.Http.HttpClient();
        var updater = DrDxt.Infrastructure.Updates.UpdateComposition.TryCreate("https://u.test", client, racine, "1.0.0",
            DrDxt.Infrastructure.Updates.UpdateDevKeys.CreateUpdateVerifier().Value!).Value!;

        Path.GetDirectoryName(updater.StagedAppPayloadPath).Should().Be(DrDxt.Infrastructure.Updates.UpdateComposition.DossierDuStagingApp(racine));
    }

    [Fact]
    public void Un_staging_sans_installeur_lance_ou_absent_ne_bouge_pas()
    {
        NettoyageDuStaging.Purger(_dossier, new Version("1.2.0"));
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Path.Combine(_dossier, "payload.pkg"), "charge téléchargée, pas encore lancée");

        NettoyageDuStaging.Purger(_dossier, new Version("1.2.0"));

        File.Exists(Path.Combine(_dossier, "payload.pkg")).Should().BeTrue();
    }
}
