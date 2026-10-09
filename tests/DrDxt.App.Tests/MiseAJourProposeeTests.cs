using System.IO;
using DrDxt.App.Updates;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// 06/10/2026 (chantier B10, plan 6.4 ; le mainteneur : « pas super souvent, et un message dans l'appli »). La vérification
/// signée existait (phase 18) ; l'écran disait « une nouvelle version signée est vérifiée » sans dire laquelle. La version
/// est nommée et exposée au contrat de l'interface ; rien ne s'installe sans le clic.
/// </summary>
public class MiseAJourProposeeTests
{
    private static ViewModels.ScanViewModel Vm()
        => ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k + " {0}");

    [Fact]
    public void Une_version_verifiee_est_nommee_et_attend_le_clic()
    {
        var vm = Vm();

        vm.AfficherMiseAJour(new ResultatMiseAJour(EtatMiseAJour.NouvelleVersionVerifiee, @"C:\staging\payload.pkg", "ab12", "0.9.1"));

        vm.VersionProposee.Should().Be("0.9.1");
        vm.LabelFooterUpdates.Should().Be("UI.Footer.Updates.NewVersionNamed 0.9.1");
        vm.CanInstallUpdate.Should().BeTrue();
    }

    // Revue (F-33) : l'installeur vérifié n'est plus là (ou a changé) ⇒ refusé ; la version n'est plus proposée.
    [Fact]
    public void Un_installeur_refuse_retire_la_proposition()
    {
        var vm = Vm();
        vm.AfficherMiseAJour(new ResultatMiseAJour(EtatMiseAJour.NouvelleVersionVerifiee,
            Path.Combine(Path.GetTempPath(), "drdxt-absent-" + Guid.NewGuid().ToString("N"), "payload.pkg"), "ab12", "0.9.1"));

        vm.InstallUpdateCommand.Execute(null);

        vm.LabelFooterUpdates.Should().StartWith("UI.Footer.Updates.InstallerRefused");
        vm.VersionProposee.Should().BeNull();
        vm.CanInstallUpdate.Should().BeFalse();
        vm.InstallUpdateCommand.CanExecute(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(EtatMiseAJour.NonConfigure)]
    [InlineData(EtatMiseAJour.Refusee)]
    [InlineData(EtatMiseAJour.AJour)]
    public void Sans_version_verifiee_rien_n_est_propose(EtatMiseAJour etat)
    {
        var vm = Vm();

        vm.AfficherMiseAJour(new ResultatMiseAJour(etat));

        vm.VersionProposee.Should().BeNull();
        vm.CanInstallUpdate.Should().BeFalse();
    }
}
