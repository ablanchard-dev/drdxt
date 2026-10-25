using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// Une opération qui mesure la machine ou y écrit longtemps tourne seule : un scan vidait ce qu'Optimiser mesurait, une
// remesure du processeur pendant le test de refroidissement enregistrait une référence faussée, un test mémoire pendant un
// palier de sous-tension lui attribuait ses erreurs. Pendant l'une, les boutons des autres se grisent.
public sealed class UneOperationLongueALaFoisTests
{
    private static ScanViewModel Vm() => ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
        Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);

    public static TheoryData<string> Operations => new()
    {
        nameof(ScanViewModel.TestRefroidissementEnCours), nameof(ScanViewModel.RechercheSousTensionEnCours),
        nameof(ScanViewModel.TestMemoireEnCours), nameof(ScanViewModel.RemesureCpuEnCours), nameof(ScanViewModel.RemesureMemoireEnCours),
        nameof(ScanViewModel.VerificationEnCours), nameof(ScanViewModel.CadenceEnCours), nameof(ScanViewModel.ReposCarteEnCours),
        nameof(ScanViewModel.ExpertEnCours), nameof(ScanViewModel.BcdEnCours),
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void Pendant_une_operation_longue_les_autres_se_grisent_puis_reviennent(string enCours)
    {
        var vm = Vm();
        var drapeau = typeof(ScanViewModel).GetProperty(enCours)!;
        vm.CanScan.Should().BeTrue();

        drapeau.SetValue(vm, true);

        vm.CanScan.Should().BeFalse();
        vm.OperationLongueEnCours.Should().BeTrue();
        foreach (var autre in new[] { vm.RemesurerProcesseurCommand, vm.LancerTestMemoireCommand, vm.LancerTestRefroidissementCommand,
                     vm.LancerRechercheSousTensionCommand, vm.DemanderVerificationCommand, vm.MesurerCadenceCommand, vm.LancerReparationCommand })
            autre.CanExecute(null).Should().BeFalse();

        drapeau.SetValue(vm, false);

        vm.CanScan.Should().BeTrue();
        vm.RemesurerProcesseurCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Un_changement_previent_les_boutons()
    {
        var vm = Vm();
        var prevenu = false;
        vm.RemesurerProcesseurCommand.CanExecuteChanged += (_, _) => prevenu = true;

        vm.TestRefroidissementEnCours = true;

        prevenu.Should().BeTrue();
    }

    // L'étape suivante et l'info-bulle d'Optimiser se jugent sur le scan, pas sur le bouton grisé : pendant une opération,
    // l'info-bulle dit l'opération (et non « lance d'abord un scan »), et après, tout revient comme avant.
    [Fact]
    public void Apres_une_operation_l_etape_et_l_info_bulle_reviennent()
    {
        var vm = Vm();
        vm.State = ScanState.Done;
        var etape = vm.ProchaineEtape;
        var cle = vm.ApplyHintKey;
        var annonces = new List<string?>();
        vm.PropertyChanged += (_, e) => annonces.Add(e.PropertyName);

        vm.TestMemoireEnCours = true;
        vm.ApplyHintKey.Should().Be("UI.OperationEnCours");
        vm.TestMemoireEnCours = false;

        vm.ProchaineEtape.Should().Be(etape);
        vm.ApplyHintKey.Should().Be(cle);
        annonces.Should().Contain(nameof(ScanViewModel.ProchaineEtape)).And.Contain(nameof(ScanViewModel.ApplyHintKey));
    }

    // Ce qui rend la machine sûre n'attend jamais une mesure : une mesure perturbée se refait, une carte instable n'attend
    // pas la fin d'un test mémoire. Annuler un réglage n'attend qu'une écriture.
    [Fact]
    public void Les_gestes_qui_rendent_restent_libres_pendant_une_operation()
    {
        var vm = Vm();
        vm.TestMemoireEnCours = true;

        foreach (var rendre in new CommunityToolkit.Mvvm.Input.IRelayCommand[]
                     { vm.RendreCarteExpertCommand, vm.RemettreLaCarteALUsineCommand, vm.AnnulerBcdCommand, vm.ArreterEssaiCommand, vm.AnnulerLevierCommand })
            rendre.CanExecute(null).Should().BeTrue();
    }
}
