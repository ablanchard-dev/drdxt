using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// La mesure de la cadence parlait de « la manette » à tout le monde : « bouge la souris et la manette » à qui n'en a pas.
// Sans manette branchée, les textes de la mesure ne parlent que de la souris.
public class CadenceSansManetteTests
{
    private static ViewModels.ScanViewModel Vm(bool? manette)
    {
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), new ScanScorer(), Substitute.For<IStorageDetector>(), k => k);
        if (manette is { } m) vm.ManetteBranchee = () => m;
        return vm;
    }

    [Fact]
    public void Sans_manette_branchee_la_mesure_ne_parle_que_de_la_souris()
    {
        var vm = Vm(false);

        new[] { vm.LabelCadenceTitre, vm.LabelCadenceDescription, vm.LabelCadenceMaintenir, vm.LabelCadenceEnCours }
            .Should().OnlyContain(l => l.EndsWith(".SansManette"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]   // non lu : le texte qui couvre les deux
    public void Avec_une_manette_ou_sans_le_savoir_elle_est_nommee(bool? manette)
    {
        var vm = Vm(manette);

        vm.LabelCadenceTitre.Should().Be("Entrees.Cadence.Titre");
        vm.LabelCadenceEnCours.Should().Be("Entrees.Cadence.EnCours");
    }
}
