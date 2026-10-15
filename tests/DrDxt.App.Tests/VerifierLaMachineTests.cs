using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// « Vérifier DrDXT sur mon PC ». Rien ne part sans l'accord du joueur, le bilan dit ce qui reste à
/// vérifier, et une vérification ratée ne bloque pas la suivante.
/// </summary>
public class VerifierLaMachineTests
{
    private static ScanViewModel Vm() => ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(),
        Substitute.For<ISettingsStateReader>(), Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(),
        Substitute.For<IStorageDetector>(), k => k switch
        {
            "Verifier.AVerifier" => "à vérifier : {0}",
            "Verifier.EnCours" => "en cours : {0}",
            "Verifier.Compte" => "{0} validés, {1} non touchés, {2} refusés",
            _ => k,
        });

    // Une ligne par réglage, avec son nom et son sort.
    [Fact]
    public async Task Le_bilan_nomme_chaque_reglage_et_ce_qui_lui_est_arrive()
    {
        var vm = Vm();
        vm.VerifierLaMachine = _ => Task.FromResult(new VerificationRendue(null, ["gpu.power_limit : PAS remis d'origine"], "r.txt", Valides: 1)
            { ValidesIds = ["latency.gameModeOn"] });

        await vm.ConfirmerVerificationCommand.ExecuteAsync(null);

        vm.LignesVerification.Should().HaveCount(DrDxt.Application.Autopilot.ValidationComplete.Leviers.Count);
        vm.LignesVerification.Should().Contain(l => l.Contains("Verifier.Ligne.Valide"), "le mode Jeu a été posé, relu, rendu")
            .And.Contain(l => l.Contains("Verifier.Ligne.AVerifier"), "la limite de puissance n'est pas revenue")
            .And.Contain(l => l.Contains("Verifier.Ligne.NonTouche"));
        vm.ADesLignesVerification.Should().BeTrue();

        vm.VerifierLaMachine = _ => Task.FromResult(new VerificationRendue("Verifier.JeuOuvert", [], null));
        await vm.ConfirmerVerificationCommand.ExecuteAsync(null);
        vm.LignesVerification.Should().HaveCount(0, "refusée avant de commencer : aucun réglage n'a été essayé");
    }

    [Fact]
    public void Sans_branchement_pas_de_bouton()
        => Vm().CanVerifierMachine.Should().BeFalse();

    [Fact]
    public async Task Rien_ne_part_avant_l_accord_puis_le_bilan_et_le_rapport_s_affichent()
    {
        var vm = Vm();
        var appels = 0;
        vm.VerifierLaMachine = progres =>
        {
            appels++;
            progres("=== latency.gameModeOn\npas touché");
            return Task.FromResult(new VerificationRendue(null, [], @"C:\Users\x\Documents\verification-drdxt.txt", Valides: 4, NonTouches: 3, Refuses: 2));
        };
        vm.CanVerifierMachine.Should().BeTrue();

        vm.DemanderVerificationCommand.Execute(null);

        vm.VerificationAConfirmer.Should().BeTrue();
        appels.Should().Be(0, "demander n'écrit rien : seule la confirmation lance la vérification");

        await vm.ConfirmerVerificationCommand.ExecuteAsync(null);

        appels.Should().Be(1);
        vm.VerificationAConfirmer.Should().BeFalse();
        vm.VerificationEnCours.Should().BeFalse();
        vm.VerificationBilan.Should().Be("4 validés, 3 non touchés, 2 refusés");
        vm.HasVerificationChemin.Should().BeTrue();
    }

    [Fact]
    public void Annuler_ne_lance_rien()
    {
        var vm = Vm();
        var appels = 0;
        vm.VerifierLaMachine = _ => { appels++; return Task.FromResult(new VerificationRendue(null, [], null)); };

        vm.DemanderVerificationCommand.Execute(null);
        vm.AnnulerVerificationCommand.Execute(null);

        vm.VerificationAConfirmer.Should().BeFalse();
        appels.Should().Be(0);
    }

    [Fact]
    public async Task Les_reglages_a_verifier_et_les_refus_sont_dits()
    {
        var vm = Vm();
        vm.VerifierLaMachine = _ => Task.FromResult(new VerificationRendue(null, ["gpu.power_limit : PAS remis d'origine"], "r.txt"));
        await vm.ConfirmerVerificationCommand.ExecuteAsync(null);
        vm.VerificationBilan.Should().Be("à vérifier : gpu.power_limit : PAS remis d'origine");

        vm.VerifierLaMachine = _ => Task.FromResult(new VerificationRendue("Verifier.JeuOuvert", [], null));
        await vm.ConfirmerVerificationCommand.ExecuteAsync(null);
        vm.VerificationBilan.Should().Be("Verifier.JeuOuvert");
        vm.HasVerificationChemin.Should().BeFalse();
    }

    // Quand rien n'a été posé (accord de Windows refusé, jeu ouvert en cours), le bilan le dit au
    // lieu d'annoncer une vérification réussie.
    [Fact]
    public async Task Un_arret_est_dit_tel_quel_jamais_comme_une_reussite()
    {
        var vm = Vm();
        vm.VerifierLaMachine = _ => Task.FromResult(new VerificationRendue(null, [], "r.txt",
            ArretPar: DrDxt.Application.Autopilot.ValidationComplete.Arret.RefusDeWindows));
        await vm.ConfirmerVerificationCommand.ExecuteAsync(null);
        vm.VerificationBilan.Should().Be("Verifier.RefusWindows");

        vm.VerifierLaMachine = _ => Task.FromResult(new VerificationRendue(null, [], "r.txt", Valides: 2,
            ArretPar: DrDxt.Application.Autopilot.ValidationComplete.Arret.JeuOuvert));
        await vm.ConfirmerVerificationCommand.ExecuteAsync(null);
        vm.VerificationBilan.Should().Be("Verifier.JeuOuvertEnCours");
    }

    // Une vérification qui lève rend le verrou des essais : la suivante part.
    [Fact]
    public async Task Une_verification_qui_echoue_ne_bloque_pas_la_suivante()
    {
        var vm = Vm();
        vm.VerifierLaMachine = _ => throw new InvalidOperationException("helper injoignable");
        await vm.ConfirmerVerificationCommand.ExecuteAsync(null);
        vm.VerificationBilan.Should().Be("Verifier.Echec");
        vm.VerificationEnCours.Should().BeFalse();

        var relancee = false;
        vm.VerifierLaMachine = _ => { relancee = true; return Task.FromResult(new VerificationRendue(null, [], null)); };
        await vm.ConfirmerVerificationCommand.ExecuteAsync(null);
        relancee.Should().BeTrue();
    }
}
