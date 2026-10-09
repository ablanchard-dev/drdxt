using System.Globalization;
using DrDxt.App.Platform;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// 06/10/2026 (chantier J1b) : une partie jouée App fermée n'était pas relevée. Relevé allumé, DrDXT pose un raccourci
// dans le dossier Démarrage du joueur (sans droits administrateur, sans registre) ; éteint, il le retire. Et l'écran le dit.
public sealed class ReleveSansLAppTests
{
    private const string ProgramFiles = @"C:\Program Files";

    [Fact]
    public void Allume_depuis_Program_Files_pose_le_raccourci_de_l_App_installee()
    {
        string? pose = null;
        ReleveAuDemarrage.Regler(true, @"C:\Program Files\DrDXT\DrDxt.App.exe", ProgramFiles, e => pose = e, () => { });
        pose.Should().Be(@"C:\Program Files\DrDXT\DrDxt.App.exe");
        ReleveAuDemarrage.Argument.Should().Be("--releve");
    }

    [Fact]
    public void Le_raccourci_pose_lance_l_App_avec_l_argument_du_releve()
    {
        // Un vrai raccourci, dans un dossier jetable : le dossier Démarrage du compte n'est pas touché.
        var dossier = System.IO.Directory.CreateTempSubdirectory("drdxt-raccourci-").FullName;
        try
        {
            var lnk = System.IO.Path.Combine(dossier, ReleveAuDemarrage.NomRaccourci);
            ReleveAuDemarrage.Poser(@"C:\Program Files\DrDXT\DrDxt.App.exe", lnk);

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            var relu = shell.CreateShortcut(lnk);
            ((string)relu.TargetPath).Should().Be(@"C:\Program Files\DrDXT\DrDxt.App.exe");
            ((string)relu.Arguments).Should().Be("--releve");
        }
        finally { System.IO.Directory.Delete(dossier, recursive: true); }
    }

    [Fact]
    public void Un_build_de_developpement_ne_s_inscrit_pas()
    {
        string? ecrit = null;
        ReleveAuDemarrage.Regler(true, @"C:\Users\x\DrDXT\bin\DrDxt.App.exe", ProgramFiles, c => ecrit = c, () => { });
        ReleveAuDemarrage.Regler(true, @"C:\Program Files (x86)\DrDxt.App.exe", ProgramFiles, c => ecrit = c, () => { });
        ecrit.Should().BeNull();
    }

    [Fact]
    public void Eteint_retire_l_inscription()
    {
        var efface = false;
        ReleveAuDemarrage.Regler(false, @"C:\ailleurs\DrDxt.App.exe", ProgramFiles, _ => { }, () => efface = true);
        efface.Should().BeTrue();
    }

    [Fact]
    public async Task Le_bouton_du_releve_inscrit_puis_retire_le_demarrage()
    {
        var vm = Neuf();
        var appels = new List<bool>();
        vm.InscrireAuDemarrage = appels.Add;

        var allume = vm.BasculerReleveDesPartiesCommand.ExecuteAsync(null);
        vm.BasculerReleveDesPartiesCommand.CanExecute(null).Should().BeTrue("relevé en cours, le bouton Arrêter reste cliquable");
        await vm.BasculerReleveDesPartiesCommand.ExecuteAsync(null);
        await allume;

        appels.Should().Equal(true, false);
    }

    [Theory]
    [InlineData("fr", "fermé")]
    [InlineData("en", "closed")]
    public void L_ecran_dit_que_le_releve_tourne_aussi_DrDXT_ferme(string culture, string mot)
    {
        var texte = new ResxLocalizer().Get("Parties.Description", new CultureInfo(culture));
        texte.Should().Contain(mot);
        texte.Should().NotContain("rester ouvert").And.NotContain("must stay open");
    }

    [Fact]
    public void La_desinstallation_retire_le_raccourci_que_l_App_pose()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var iss = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "installer", "DrDXT.iss"));

        iss.Should().Contain("[UninstallDelete]")
            .And.Contain($"Type: files; Name: \"{{userstartup}}\\{ReleveAuDemarrage.NomRaccourci}\"");
    }

    private static ScanViewModel Neuf() => new(
        Substitute.For<IHardwareDetector>(),
        Substitute.For<ISettingsStateReader>(),
        Substitute.For<IBenchmarkRunner>(),
        Substitute.For<IScanScorer>(),
        Substitute.For<IStorageDetector>(),
        ScanViewModelTestSeams.Safety(),
        ScanViewModelTestSeams.Engine(),
        ScanViewModelTestSeams.ProfileConsumer(),
        ScanViewModelTestSeams.GpuTuningStep(),
        Substitute.For<DrDxt.App.Navigation.INavigationService>(),
        () => new DashboardViewModel(),
        ScanViewModelTestSeams.DesiredStateStore(),
        new AppliedTweakManifestMapper(),
        ScanViewModelTestSeams.BiosAdvisor(),
        ScanViewModelTestSeams.GuideGenerator(),
        cle => cle);
}
