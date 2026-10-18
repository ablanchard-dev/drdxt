using System.Linq;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// La jauge du scan suit le vrai travail : une étape qui va lancer un banc le dit, et la jauge prévoit sa vraie durée au
// lieu de rester figée à 95 % de l'étape une minute durant.
public sealed class JaugeDuScanTests
{
    private static ScanViewModel Vm() => ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(),
        Substitute.For<ISettingsStateReader>(), Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(),
        Substitute.For<IStorageDetector>(), k => k);

    [Fact]
    public void Une_etape_qui_annonce_un_banc_prend_sa_vraie_duree_dans_la_jauge()
    {
        var vm = Vm();
        vm.Etape(1, "a");
        vm.Etape(4, "b");
        var vitesseAvant = vm.VitesseDuScan;

        vm.Prevoir(4, ScanViewModel.DureeBancProcesseur);

        double[] durees = [0.1, 2.7, 0.05, ScanViewModel.DureeBancProcesseur, 0.1, 0.6, 0.1, 3.2];
        var (debut, fin) = ScanViewModel.Bornes(durees, 4);
        vm.VitesseDuScan.Should().BeApproximately((fin - debut) / ScanViewModel.DureeBancProcesseur, 1e-9);
        vm.PlafondDuScan.Should().BeApproximately(debut + (fin - debut) * 0.95, 1e-9);
        vm.VitesseDuScan.Should().BeLessThan(vitesseAvant, "témoin : prévue courte, l'étape avançait bien plus vite");
        vm.ProgressionDuScan.Should().BeLessThanOrEqualTo(vm.PlafondDuScan);
    }

    [Fact]
    public void Une_prevision_pour_une_autre_etape_ne_change_rien()
    {
        var vm = Vm();
        vm.Etape(1, "a");
        vm.Etape(4, "b");
        var (vitesse, plafond) = (vm.VitesseDuScan, vm.PlafondDuScan);

        vm.Prevoir(5, ScanViewModel.DureeBancMemoire);

        (vm.VitesseDuScan, vm.PlafondDuScan).Should().Be((vitesse, plafond));
    }
}
