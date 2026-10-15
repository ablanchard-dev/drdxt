using System.IO;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Le rapport part par un ticket GitHub pré-rempli,
/// sans serveur ; DrDXT n'envoie rien lui-même, le joueur relit et soumet.</summary>
public sealed class LienDeRapportTests
{
    [Fact]
    public void Le_lien_ouvre_le_modele_avec_le_titre_et_le_rapport()
    {
        var lien = LienDeRapport.Construire("Core i7-14700F", "NVIDIA GeForce RTX 4060", "BILAN : tout est remis d'origine.\n=== gpu.power_limit");

        lien.Should().StartWith($"https://github.com/{LienDeRapport.Depot}/issues/new?template=rapport-de-verification.yml");
        var decode = Uri.UnescapeDataString(lien);
        decode.Should().Contain("title=Vérification : Core i7-14700F + NVIDIA GeForce RTX 4060")
            .And.Contain("rapport=BILAN : tout est remis d'origine.\n=== gpu.power_limit");
    }

    [Fact]
    public void Un_long_rapport_est_coupe_et_le_dit()
    {
        var lien = LienDeRapport.Construire("cpu", "gpu", new string('x', 20_000));
        lien.Length.Should().BeLessThanOrEqualTo(LienDeRapport.LongueurMax);
        Uri.UnescapeDataString(lien).Should().Contain("rapport coupé");
    }

    [Fact]
    public void Le_bouton_ouvre_le_lien_du_rapport_ecrit()
    {
        var chemin = Path.Combine(Path.GetTempPath(), "drdxt-rapport-test-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(chemin, "BILAN : tout est remis d'origine.");
        try
        {
            var vm = ScanViewModelTestSeams.Build(NSubstitute.Substitute.For<DrDxt.Core.IHardwareDetector>(),
                NSubstitute.Substitute.For<DrDxt.Core.ISettingsStateReader>(), NSubstitute.Substitute.For<DrDxt.Core.IBenchmarkRunner>(),
                NSubstitute.Substitute.For<DrDxt.Core.IScanScorer>(), NSubstitute.Substitute.For<DrDxt.Core.IStorageDetector>(), k => k);
            string? ouvert = null;
            vm.OuvrirLien = l => { ouvert = l; return true; };
            vm.VerificationChemin = chemin;

            vm.EnvoyerRapportCommand.Execute(null);

            Uri.UnescapeDataString(ouvert!).Should().Contain("rapport=BILAN : tout est remis d'origine.");
        }
        finally { File.Delete(chemin); }
    }

    /// <summary>Ce qui se fait à la main se lit sur le dépôt, depuis la liste des leviers.</summary>
    [Fact]
    public void Le_guide_de_ce_qui_se_fait_a_la_main_s_ouvre_depuis_l_App()
    {
        var vm = ScanViewModelTestSeams.Build(NSubstitute.Substitute.For<DrDxt.Core.IHardwareDetector>(),
            NSubstitute.Substitute.For<DrDxt.Core.ISettingsStateReader>(), NSubstitute.Substitute.For<DrDxt.Core.IBenchmarkRunner>(),
            NSubstitute.Substitute.For<DrDxt.Core.IScanScorer>(), NSubstitute.Substitute.For<DrDxt.Core.IStorageDetector>(), k => k);
        string? ouvert = null;
        vm.OuvrirLien = l => { ouvert = l; return true; };

        vm.OuvrirGuideCommand.Execute(null);

        ouvert.Should().Be($"https://github.com/{LienDeRapport.Depot}/blob/main/docs/A-FAIRE-A-LA-MAIN.md");
        File.Exists(Path.Combine(Racine(), "docs", "A-FAIRE-A-LA-MAIN.md")).Should().BeTrue("le lien vise un fichier du dépôt");
    }

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
