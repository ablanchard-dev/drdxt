using System.IO;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// L'App se relance élevée par le lanceur natif quand il est installé à côté d'elle.
public sealed class RelanceEleveeTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-relance-" + Guid.NewGuid().ToString("N"));

    public RelanceEleveeTests() => Directory.CreateDirectory(_dossier);
    public void Dispose() => Directory.Delete(_dossier, true);

    [Fact]
    public void Avec_le_lanceur_a_cote_l_app_est_son_premier_argument()
    {
        var app = Path.Combine(_dossier, "DrDxt.App.exe");
        File.WriteAllText(app, "");
        File.WriteAllText(Path.Combine(_dossier, RelanceElevee.Lanceur), "");

        var (fichier, prefixe) = RelanceElevee.Cible(app);

        fichier.Should().Be(Path.Combine(_dossier, "DrDxt.Lanceur.exe"));
        prefixe.Should().Equal("DrDxt.App.exe");
    }

    [Fact]
    public void Sans_lanceur_l_app_se_relance_elle_meme()
    {
        var app = Path.Combine(_dossier, "DrDxt.App.exe");
        File.WriteAllText(app, "");

        RelanceElevee.Cible(app).Should().Be((app, (IReadOnlyList<string>)[]));
    }
}
