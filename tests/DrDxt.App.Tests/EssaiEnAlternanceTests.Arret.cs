using DrDxt.Tests.Shared;
using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Arrêter ou annuler l'essai : l'état d'avant rendu, relu, et dit honnêtement.</summary>
public sealed partial class EssaiEnAlternanceTests
{
    [Fact]
    public async Task Sans_helper_deja_lance_l_essai_se_met_en_pause_sans_invite_windows()
    {
        var vm = Vm(helperJoignable: false);
        await vm.DemarrerEssaiAsync(Id);

        await Jouer(vm, 90);

        _rendus.Should().Be(0, "rendre le levier aurait demandé une autorisation Windows en jeu");
        vm.LigneEssai.Should().Be("Essai.Pause");
        vm.MemoireEssai!.Lire()!.EnPause.Should().BeTrue();
        await Jouer(vm, 80);
        // En pause, le levier est resté posé alors que les blocs voulaient la partie sans : jouée hors du plan, elle ne
        // compte d'aucun côté (elle fausserait l'alternance A-B-B-A).
        vm.MemoireParties!.Lire().OrderBy(p => p.Debut).Last().Essai.Should().BeNull();
    }

    // La mémoire de l'essai a manqué une bascule (l'App tuée entre la pose et son écriture, ou l'écriture refusée) : elle dit
    // « pas posé » alors que le levier l'est. L'arrêt relit l'état réel, et rend ce qui est vraiment posé.
    [Fact]
    public async Task Arreter_relit_l_etat_reel_quand_la_memoire_a_manque_une_pose()
    {
        var vm = Vm();
        vm.VerifierLaPose = _ => _rendus == 0 ? EtatApplique.Applied : EtatApplique.NotApplied;
        await vm.DemarrerEssaiAsync(Id);
        vm.MemoireEssai!.Ecrire(vm.MemoireEssai.Lire()! with { PoseMaintenant = false });

        await vm.ArreterEssaiAsync();

        _rendus.Should().Be(1, "le levier était posé, quoi qu'en dise la mémoire");
        vm.LigneEssai.Should().Be("Essai.Arrete");
    }

    // L'état réel ne se relit pas : rien n'est posé ni rendu au hasard, et la ligne ne dit pas « l'état d'avant est revenu ».
    // Elle dit que rien n'a été rendu, et renvoie vers « Annuler ».
    [Fact]
    public async Task Arreter_sans_pouvoir_relire_ne_promet_pas_le_retour()
    {
        var vm = Vm();
        vm.VerifierLaPose = _ => EtatApplique.Applied;
        await vm.DemarrerEssaiAsync(Id);
        vm.MemoireEssai!.Ecrire(vm.MemoireEssai.Lire()! with { PoseMaintenant = false });
        vm.VerifierLaPose = _ => EtatApplique.Unknown;

        await vm.ArreterEssaiAsync();

        _rendus.Should().Be(0);
        vm.LigneEssai.Should().Be("Essai.ArreteSansRelecture");
    }

    // Arrêter : le rendu a réussi mais sa relecture est passagèrement illisible. Une relecture de plus ; toujours illisible,
    // l'arrêt se fait et dit « non relu », jamais « raté ».
    [Theory]
    [InlineData(true, "Essai.Arrete")]
    [InlineData(false, "Essai.ArreteNonRelu")]
    public async Task Arreter_sur_une_relecture_passagerement_illisible(bool relueAuSecondEssai, string dit)
    {
        var vm = Vm();
        var lectures = 0;
        vm.VerifierLaPose = _ => _rendus == 0 ? EtatApplique.Applied
            : ++lectures == 1 || !relueAuSecondEssai ? EtatApplique.Unknown : EtatApplique.NotApplied;
        await vm.DemarrerEssaiAsync(Id);

        await vm.ArreterEssaiAsync();

        vm.LigneEssai.Should().Be(dit);
        vm.EssaiEnCours.Should().BeFalse();
    }

    // La mémoire de l'essai ne s'écrit pas : l'essai ne part pas, ce qui vient d'être posé est rendu, et c'est dit.
    [Fact]
    public async Task Un_essai_qui_ne_peut_pas_etre_note_ne_part_pas()
    {
        var vm = Vm();
        var occupe = Path.Combine(_dossier, "essai-dossier");
        Directory.CreateDirectory(occupe);
        vm.MemoireEssai = new MemoireDeLEssai(occupe);

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        (_poses, _rendus).Should().Be((1, 1));
        vm.LigneEssai.Should().Be("Essai.NonNote");
    }

    [Fact]
    public async Task Arreter_l_essai_rend_l_etat_d_avant()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);

        await vm.ArreterEssaiAsync();

        _rendus.Should().Be(1, "avant l'essai le levier n'était pas posé");
        vm.EssaiEnCours.Should().BeFalse();
        vm.LigneEssai.Should().Be("Essai.Arrete");
    }

    // Un essai d'un levier qui n'alterne pas (fichier forgé) ou inconnu s'efface sans rien toucher : « l'état d'avant est
    // revenu » serait faux, rien n'a été rendu.
    [Theory]
    [InlineData("latency.hagsOn")]
    [InlineData("inconnu")]
    public async Task Arreter_l_essai_d_un_levier_qui_n_alterne_pas_ne_dit_pas_l_etat_d_avant_revenu(string id)
    {
        var vm = Vm();
        vm.MemoireEssai!.Ecrire(new EssaiEnAlternance(id, PoseALOrigine: false, PoseMaintenant: true, _maintenant));

        await vm.ArreterEssaiAsync();

        _rendus.Should().Be(0);
        vm.MemoireEssai.Lire().Should().BeNull();
        vm.LigneEssai.Should().Be("Essai.ArreteSansRien");
    }

    // Annulé pendant l'essai, le levier est à la valeur d'avant DrDXT, pas forcément à l'état d'avant l'essai (déjà posé
    // avant lui) : la ligne dit l'annulation, rien de plus.
    [Fact]
    public async Task Annuler_un_levier_pose_avant_l_essai_ne_dit_pas_l_etat_d_avant_l_essai_revenu()
    {
        var vm = Vm();
        new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json"))
            .Enregistrer(new ApplyReport([new ApplyOutcome(Id, true, false, new TweakSnapshot(Id, InversesDeTest.Pour(Id, "100"), "90"), null)], null));
        await vm.DemarrerEssaiAsync(Id);
        vm.MemoireEssai!.Lire()!.PoseALOrigine.Should().BeTrue("témoin : posé avant l'essai");

        await vm.AnnulerLevierCommand.ExecuteAsync(Id);

        vm.EssaiEnCours.Should().BeFalse();
        vm.LigneEssai.Should().Be("Essai.ArreteParAnnulation");
    }

    [Fact]
    public void Seul_un_levier_a_chaud_s_essaie_en_alternance()
    {
        ScanViewModel.PeutAlterner(Id).Should().BeTrue();
        ScanViewModel.PeutAlterner("latency.hagsOn").Should().BeFalse("HAGS demande un redémarrage");
        ScanViewModel.PeutAlterner("inconnu").Should().BeFalse();
    }
    // La veille enregistre la partie hors du fil de l'écran ; l'avancée de l'essai (qui rafraîchit des
    // listes liées à l'écran) doit y être postée, comme l'affichage.
    private sealed class FilDeLEcran : SynchronizationContext
    {
        public readonly Queue<(SendOrPostCallback, object?)> Postes = new();
        public override void Post(SendOrPostCallback d, object? state) => Postes.Enqueue((d, state));
        public void Vider() { while (Postes.Count > 0) { var (d, st) = Postes.Dequeue(); d(st); } }
    }

    [Fact]
    public async Task L_avancee_de_l_essai_passe_par_le_fil_de_l_ecran()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        var ui = new FilDeLEcran();
        _maintenant = _maintenant.AddDays(1);

        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 90,
            BottleneckState.CpuBoundOrAppBound), ui);

        _rendus.Should().Be(0, "rien ne bascule hors du fil de l'écran");
        ui.Vider();
        await vm.DerniereAvanceeEssai;
        _rendus.Should().Be(1);
    }

    // « Annuler » sur le levier en essai arrête l'essai ; rien ne le repose ensuite.
    [Fact]
    public async Task Annuler_le_levier_en_essai_arrete_l_essai_et_rien_ne_le_repose()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);

        await vm.AnnulerLevierCommand.ExecuteAsync(Id);
        await Jouer(vm, 90);
        await Jouer(vm, 80);

        _poses.Should().Be(1, "seul le clic d'origine l'a posé");
        vm.EssaiEnCours.Should().BeFalse();
    }

    // Sans jeu ouvert, Optimiser lance l'essai.
    [Fact]
    public async Task Sans_jeu_ouvert_optimiser_lance_l_essai_du_reglage_qui_se_juge_en_jeu()
    {
        var vm = Vm();
        vm.LevierAEssayer = Id;

        (await vm.EssayerEnJeuSansJeuOuvertAsync()).Should().BeTrue();

        vm.EssaiEnCours.Should().BeTrue();
        _poses.Should().Be(1, "le clic pose le levier : la première partie l'a avec");
        vm.ResultatOptimisation.Should().Be("Apply.EssaiLanceSansJeu", "l'écran dit ce qui se passe, et quoi faire");
        (await vm.EssayerEnJeuSansJeuOuvertAsync()).Should().BeFalse("un seul essai à la fois");
    }
}
