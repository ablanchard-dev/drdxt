using System.IO;
using DrDxt.App.Updates;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Une version signée et vérifiée est nommée (« une nouvelle version » ne dirait pas laquelle) et exposée au
/// contrat de l'interface ; rien ne s'installe sans le clic.
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

    // L'installeur vérifié n'est plus là (ou a changé) ⇒ refusé ; la version n'est plus proposée.
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
