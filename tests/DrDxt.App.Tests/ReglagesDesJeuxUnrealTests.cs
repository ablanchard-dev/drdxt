using System.IO;
using DrDxt.App;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Un fichier de réglages illisible (le jeu l'écrit pendant le scan) se dit « non lu » pour ce jeu seul.
public sealed class ReglagesDesJeuxUnrealTests : IDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"drdxt-gus-{Guid.NewGuid():N}.ini");
    public void Dispose() => File.Delete(_fichier);

    [Fact]
    public void Un_fichier_verrouille_dit_non_lu_et_un_fichier_lisible_se_lit()
    {
        File.WriteAllText(_fichier, "bUseVSync=False\nFullscreenMode=0\nFrameRateLimit=141.000000\n");

        AppServiceCollectionExtensions.ConstatUnreal("JeuBClient-Win64-Shipping.exe", new FileInfo(_fichier))
            .ValeurCourante.Should().StartWith("V-Sync coupé ; plafond d'images 141 images/s ; affichage plein écran");

        using (new FileStream(_fichier, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var c = AppServiceCollectionExtensions.ConstatUnreal("JeuBClient-Win64-Shipping.exe", new FileInfo(_fichier));
            c.Statut.Should().Be(RepairStatus.Unknown);
            c.ValeurCourante.Should().BeNull();
            c.Nom.Should().Be("réglages de JeuB");
        }
    }
}
