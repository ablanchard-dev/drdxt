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

    // Un fichier au-delà du plafond des fichiers tiers n'est pas lu : non lu, sans charger des Go en mémoire.
    [Fact]
    public void Un_fichier_trop_gros_dit_non_lu()
    {
        File.WriteAllText(_fichier, "FrameRateLimit=141\n" + new string(';', (int)DrDxt.Detection.FichierTiers.OctetsMax));

        AppServiceCollectionExtensions.ConstatUnreal("JeuBClient-Win64-Shipping.exe", new FileInfo(_fichier))
            .Statut.Should().Be(RepairStatus.Unknown);
    }

    // Call of Duty est lu dans le même lot que les jeux Unreal : son fichier illisible se dit non lu, sans lever.
    [Fact]
    public void Le_fichier_de_call_of_duty_verrouille_dit_non_lu_sans_lever()
    {
        File.WriteAllText(_fichier, "NvidiaReflex@0;1;0 = \"Enabled\"\n");

        using (new FileStream(_fichier, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            AppServiceCollectionExtensions.ConstatsCod(new FileInfo(_fichier))
                .Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown);
    }
}
