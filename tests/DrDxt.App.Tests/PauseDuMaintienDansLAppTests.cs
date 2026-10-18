using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>La bascule « mettre le maintien en pause » : visible seulement si le service de démarrage est installé, posée
/// par le helper, et son état relu après le geste plutôt que supposé.</summary>
public sealed class PauseDuMaintienDansLAppTests
{
    private EtatDeLaPause _etat = EtatDeLaPause.Actif;

    private static ScanViewModel Vm(IElevatedClient? helper) => ScanViewModelTestSeams.Build(
        Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(), Substitute.For<IBenchmarkRunner>(),
        Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k, helper: helper);

    private IElevatedClient Helper(Func<SetMaintenancePauseOp, Result> reponse)
    {
        var h = Substitute.For<IElevatedClient>();
        h.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(reponse((SetMaintenancePauseOp)c.Arg<PrivilegedOp>())));
        return h;
    }

    private ScanViewModel Branche(IElevatedClient helper, bool installe = true)
    {
        var vm = Vm(helper);
        vm.ServiceInstalle = () => installe;
        vm.LirePauseDuMaintien = () => _etat;
        return vm;
    }

    [Fact]
    public void La_bascule_ne_se_montre_que_si_le_service_est_installe()
    {
        var helper = Helper(_ => Result.Ok());

        Branche(helper, installe: false).PauseMaintienVisible.Should().BeFalse();
        Branche(helper).PauseMaintienVisible.Should().BeTrue();
        Branche(helper).CarteWindowsVisible.Should().BeTrue();
        Vm(helper).PauseMaintienVisible.Should().BeFalse("sans lecture de l'état du service, rien n'est montré");
    }

    [Fact]
    public async Task Le_geste_pose_la_pause_par_le_helper_et_relit_l_etat()
    {
        var vm = Branche(Helper(op => { _etat = op.EnPause ? EtatDeLaPause.EnPause : EtatDeLaPause.Actif; return Result.Ok(); }));
        vm.LabelPauseMaintienBascule.Should().Be("Maintien.Pause.Poser");
        var annonces = new List<string?>();
        vm.PropertyChanged += (_, e) => annonces.Add(e.PropertyName);

        await vm.BasculerPauseMaintienCommand.ExecuteAsync(null);

        vm.MaintienEnPause.Should().BeTrue();
        vm.LabelPauseMaintienBascule.Should().Be("Maintien.Pause.Reprendre");
        annonces.Should().Contain(nameof(ScanViewModel.MaintienEnPause));

        await vm.BasculerPauseMaintienCommand.ExecuteAsync(null);
        vm.MaintienEnPause.Should().BeFalse();
    }

    [Fact]
    public async Task Un_refus_du_helper_est_dit_et_l_etat_affiche_reste_celui_relu()
    {
        var vm = Branche(Helper(_ => Result.Fail(ErrorCodes.IoFailure, "access denied")));

        await vm.BasculerPauseMaintienCommand.ExecuteAsync(null);

        vm.MaintienEnPause.Should().BeFalse();
        vm.ErreurPauseMaintien.Should().StartWith("Maintien.Pause.Echec").And.NotContain("access denied");
    }

    // Un fichier de pause qui n'est pas aux administrateurs vaut pause côté service. L'App ne le dit ni actif ni en pause :
    // elle dit le doute et propose les deux gestes, qui réécrivent un fichier de confiance ou l'effacent.
    [Theory]
    [InlineData(true, EtatDeLaPause.EnPause)]
    [InlineData(false, EtatDeLaPause.Actif)]
    public async Task Une_pause_incertaine_se_dit_et_se_remet_ou_se_retire(bool remettre, EtatDeLaPause attendu)
    {
        _etat = EtatDeLaPause.NonFiable;
        var helper = Helper(op => { _etat = op.EnPause ? EtatDeLaPause.EnPause : EtatDeLaPause.Actif; return Result.Ok(); });
        var vm = Branche(helper);

        vm.PauseIncertaine.Should().BeTrue();
        vm.MaintienEnPause.Should().BeFalse();
        vm.LabelPauseMaintienEtat.Should().Be("Maintien.Pause.Incertaine");
        vm.LabelPauseMaintienBascule.Should().Be("Maintien.Pause.Remettre");

        await (remettre ? vm.BasculerPauseMaintienCommand : vm.RetirerPauseMaintienCommand).ExecuteAsync(null);

        await helper.Received(1).ExecuteAsync(new SetMaintenancePauseOp(remettre), Arg.Any<CancellationToken>());
        _etat.Should().Be(attendu);
        vm.PauseIncertaine.Should().BeFalse();
    }
}
