using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le depart vers le BIOS n'etait lie a aucune vue. Ce bouton redemarre la machine :
// deux clics (dire, puis faire), et rien ne part tant que le second n'a pas eu lieu.
public sealed class BiosGuideDepartTests
{
    private static PersonalizedGuide Guide() => ScanViewModelTestSeams.Guide(
        GuideConfidence.Verified, false, "Carte",
        ScanViewModelTestSeams.Step(FirmwareSettingKind.XmpExpo, "Bios.Guide.Step.Xmp", "Ai Tweaker", 1));

    private static (BiosGuideViewModel vm, List<int> appels) Monter(Func<Task<Result>>? depart)
    {
        var appels = new List<int>();
        var vm = depart is null
            ? new BiosGuideViewModel(Guide(), k => k)
            : new BiosGuideViewModel(Guide(), k => k, delai => { appels.Add(delai); return depart(); });
        return (vm, appels);
    }

    [Fact]
    public void Sans_couture_aucun_bouton_et_le_clic_ne_fait_rien()
    {
        var (vm, _) = Monter(null);

        vm.BoutonDepartVisible.Should().BeFalse();
        vm.DemanderDepartCommand.Execute(null);
        vm.Depart.Should().Be(BiosGuideViewModel.EtatDepart.Repos);
    }

    [Fact]
    public void Le_premier_clic_demande_confirmation_et_annuler_ne_lance_rien()
    {
        var (vm, appels) = Monter(() => Task.FromResult(Result.Ok()));

        vm.BoutonDepartVisible.Should().BeTrue();
        vm.DemanderDepartCommand.Execute(null);
        vm.ConfirmationVisible.Should().BeTrue();
        vm.BoutonDepartVisible.Should().BeFalse();

        vm.AnnulerDepartCommand.Execute(null);

        vm.Depart.Should().Be(BiosGuideViewModel.EtatDepart.Repos);
        appels.Should().BeEmpty("rien ne redemarre sans le second clic");
    }

    [Fact]
    public async Task Confirmer_lance_le_depart_une_fois_avec_le_delai_annonce()
    {
        var (vm, appels) = Monter(() => Task.FromResult(Result.Ok()));
        vm.DemanderDepartCommand.Execute(null);

        await vm.ConfirmerDepartCommand.ExecuteAsync(null);

        appels.Should().Equal(BiosGuideViewModel.DefaultFirmwareRebootDelaySeconds);
        vm.DepartLanceVisible.Should().BeTrue();
    }

    [Fact]
    public async Task Pendant_la_mesure_l_ecran_le_dit()
    {
        var fin = new TaskCompletionSource<Result>();
        var (vm, _) = Monter(() => fin.Task);
        vm.DemanderDepartCommand.Execute(null);

        var en_cours = vm.ConfirmerDepartCommand.ExecuteAsync(null);
        vm.DepartEnCoursVisible.Should().BeTrue();
        vm.ConfirmationVisible.Should().BeFalse();

        fin.SetResult(Result.Ok());
        await en_cours;
        vm.DepartLanceVisible.Should().BeTrue();
    }

    [Fact]
    public async Task Un_refus_revient_au_bouton_et_dit_pourquoi()
    {
        var (vm, _) = Monter(() => Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "UAC refusée")));
        vm.DemanderDepartCommand.Execute(null);

        await vm.ConfirmerDepartCommand.ExecuteAsync(null);

        vm.BoutonDepartVisible.Should().BeTrue();
        vm.ErreurDepart.Should().Contain("Bios.Depart.Echec").And.Contain("Erreur.Generique").And.NotContain("UAC refusée");
    }

    [Fact]
    public async Task Une_exception_ne_casse_pas_l_ecran()
    {
        var (vm, _) = Monter(() => throw new InvalidOperationException("tube fermé"));
        vm.DemanderDepartCommand.Execute(null);

        await vm.ConfirmerDepartCommand.ExecuteAsync(null);

        vm.BoutonDepartVisible.Should().BeTrue();
        vm.ErreurDepart.Should().Contain("Erreur.Generique").And.NotContain("tube fermé");
    }

    [Fact]
    public async Task Apres_le_lancement_on_peut_encore_se_retracter()
    {
        var annulations = 0;
        var vm = new BiosGuideViewModel(Guide(), k => k, _ => Task.FromResult(Result.Ok()),
            () => { annulations++; return Task.FromResult(Result.Ok()); });
        vm.DemanderDepartCommand.Execute(null);
        await vm.ConfirmerDepartCommand.ExecuteAsync(null);
        vm.AnnulationVisible.Should().BeTrue();

        await vm.AnnulerRedemarrageCommand.ExecuteAsync(null);

        annulations.Should().Be(1);
        vm.BoutonDepartVisible.Should().BeTrue();
        vm.AnnulationVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Une_annulation_ratee_le_dit_et_ne_pretend_pas_etre_revenue_au_repos()
    {
        var vm = new BiosGuideViewModel(Guide(), k => k, _ => Task.FromResult(Result.Ok()),
            () => Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "shutdown /a refusé")));
        vm.DemanderDepartCommand.Execute(null);
        await vm.ConfirmerDepartCommand.ExecuteAsync(null);

        await vm.AnnulerRedemarrageCommand.ExecuteAsync(null);

        vm.DepartLanceVisible.Should().BeTrue();
        vm.ErreurDepart.Should().Contain("Bios.Depart.AnnulationEchec").And.Contain("Erreur.Generique").And.NotContain("shutdown /a refusé");
    }

    [Fact]
    public async Task Sans_couture_d_annulation_pas_de_lien()
    {
        var (vm, _) = Monter(() => Task.FromResult(Result.Ok()));
        vm.DemanderDepartCommand.Execute(null);
        await vm.ConfirmerDepartCommand.ExecuteAsync(null);

        vm.AnnulationVisible.Should().BeFalse();
    }
}
