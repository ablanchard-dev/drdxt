using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Redémarrage, réveil et relevé de fond pendant l'essai.</summary>
public sealed partial class EssaiEnAlternanceTests
{
    [Fact]
    public async Task Sans_reglage_a_essayer_optimiser_ne_lance_rien()
    {
        var vm = Vm();
        vm.LevierAEssayer = null;
        (await vm.EssayerEnJeuSansJeuOuvertAsync()).Should().BeFalse();
        _poses.Should().Be(0);
    }

    // Un réglage qui survit au redémarrage (plan d'alimentation) : l'état n'est plus sûr, l'essai s'arrête et le dit.
    private const string Durable = "fixit.power_plan";

    [Fact]
    public async Task Un_redemarrage_pendant_l_essai_l_arrete_sans_marquer_la_partie()
    {
        var vm = Vm(id: Durable);
        await vm.DemarrerEssaiAsync(Durable);
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);

        vm.MemoireParties!.Lire()[0].Essai.Should().BeNull();
        vm.EssaiEnCours.Should().BeFalse();
        vm.LigneEssai.Should().Be("Essai.Redemarrage");
        (_poses - _rendus).Should().Be(0, "posé au moment du redémarrage, il est rendu : un plan d'alimentation survit au redémarrage");
    }

    // Sans helper joignable, rien ne s'efface : l'essai attend que le joueur clique « Arrêter ».
    [Fact]
    public async Task Apres_un_redemarrage_sans_helper_l_essai_attend_le_clic_qui_rend_l_origine()
    {
        var vm = Vm(helperJoignable: false, id: Durable);
        await vm.DemarrerEssaiAsync(Durable);
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);

        vm.EssaiEnCours.Should().BeTrue();
        vm.LigneEssai.Should().Be("Essai.RedemarrageARendre");
        _rendus.Should().Be(0);

        await vm.ArreterEssaiAsync();
        _rendus.Should().Be(1);
        vm.EssaiEnCours.Should().BeFalse();
    }

    // Un réglage
    // VOLATIL (limite de la carte, PL1) s'efface au redémarrage ; son état est alors connu sans rien écrire :
    // « sans ». L'essai continue. La partie d'après, jouée sans alors que les blocs la voulaient avec, ne compte d'aucun côté
    // (comptée « sans », elle avançait les blocs sans les suivre, et un joueur qui éteint le soir n'accumulait que des « sans ») ;
    // l'App ouverte repose le réglage pour la suivante.
    // Après une sortie de veille, rien n'est supposé : le réglage volatil se relit. Relu rendu, la partie suivante, jouée sans
    // alors que les blocs la voulaient avec, ne compte d'aucun côté, et l'essai l'attend.
    [Fact]
    public async Task Apres_un_reveil_un_reglage_volatil_relu_rendu_est_pas_pose()
    {
        var vm = Vm();
        vm.VerifierLaPose = _ => EtatApplique.Applied;
        await vm.DemarrerEssaiAsync(Id);
        vm.DerniereSortieDeVeille = () => _maintenant.AddHours(2);
        vm.VerifierLaPose = _ => EtatApplique.NotApplied;
        vm.LevierParId = null; // le relevé de fond : il note, ne pose rien

        await Jouer(vm, 90);

        vm.MemoireParties!.Lire()[0].Essai.Should().BeNull("jouée sans le réglage alors que les blocs le voulaient");
        vm.EssaiEnAttente.Should().BeTrue();
    }

    [Fact]
    public async Task Apres_un_reveil_un_reglage_volatil_relu_en_place_compte()
    {
        var vm = Vm();
        vm.VerifierLaPose = _ => EtatApplique.Applied;
        await vm.DemarrerEssaiAsync(Id);
        vm.DerniereSortieDeVeille = () => _maintenant.AddHours(2);
        vm.LevierParId = null;

        await Jouer(vm, 90);

        vm.MemoireParties!.Lire()[0].Essai.Should().Be(AlternanceDesEssais.Marque(Id, true));
    }

    // Relu ni posé ni rendu après un réveil : la partie ne compte d'aucun côté.
    [Fact]
    public async Task Apres_un_reveil_un_reglage_volatil_non_relu_ne_compte_pas()
    {
        var vm = Vm();
        vm.VerifierLaPose = _ => EtatApplique.Applied;
        await vm.DemarrerEssaiAsync(Id);
        vm.DerniereSortieDeVeille = () => _maintenant.AddHours(2);
        vm.VerifierLaPose = _ => EtatApplique.Unknown;
        vm.LevierParId = null;

        await Jouer(vm, 90);

        vm.MemoireParties!.Lire()[0].Essai.Should().BeNull();
    }

    [Fact]
    public async Task Un_redemarrage_rend_un_reglage_volatil_a_l_origine_et_l_essai_continue()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        _poses.Should().Be(1);
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);

        vm.EssaiEnCours.Should().BeTrue("le redémarrage n'arrête plus l'essai d'un réglage volatil");
        vm.MemoireParties!.Lire()[0].Essai.Should().BeNull("jouée sans le réglage alors que les blocs le voulaient");
        _rendus.Should().Be(0, "rien à rendre : le redémarrage l'a déjà fait");
        _poses.Should().Be(2, "les blocs le veulent toujours : l'App ouverte le repose pour la partie suivante");
    }

    // Le cas réel : le relevé de fond (aucun levier accessible) enregistre la partie. Il ne pose rien, mais il note l'état vrai,
    // et l'essai attend l'App ouverte.
    [Fact]
    public async Task Le_releve_de_fond_note_un_reglage_volatil_efface_sans_rien_poser()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        vm.LevierParId = null; // le relevé de fond
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);

        vm.MemoireEssai!.Lire()!.PoseMaintenant.Should().BeFalse("le redémarrage l'a effacé : l'essai ne reste plus figé sur « posé »");
        vm.MemoireParties!.Lire()[0].Essai.Should().BeNull("jouée sans le réglage alors que les blocs le voulaient");
        (_poses, _rendus).Should().Be((1, 0), "le relevé de fond ne touche à aucun levier");
        vm.EssaiEnAttente.Should().BeTrue();
    }

    // Le relevé de fond ne pose rien : quand l'essai attend l'App ouverte, il prévient le joueur une fois, jeu fermé, au lieu de
    // laisser l'essai figé en silence.
    [Fact]
    public async Task Le_releve_de_fond_previent_une_fois_quand_l_essai_attend_l_App()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        vm.LevierParId = null; // le relevé de fond
        var notes = new List<string>();
        vm.NotifierEssaiEnAttente = notes.Add;
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);
        notes.Should().ContainSingle().Which.Should().Be("Essai.Notification");
        vm.EssaiEnAttente.Should().BeTrue("témoin : l'essai attend bien l'App");

        await Jouer(vm, 91);
        notes.Should().HaveCount(1, "le même essai en attente ne se redit pas à chaque partie");
    }

    // L'App ouverte montre déjà l'essai en attente : le relevé ne double pas son écran d'une notification.
    [Fact]
    public async Task App_ouverte_le_releve_ne_previent_pas()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        vm.LevierParId = null;
        var notes = new List<string>();
        vm.NotifierEssaiEnAttente = notes.Add;
        vm.AppOuverte = () => true;
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);

        notes.Should().BeEmpty();
        vm.EssaiEnAttente.Should().BeTrue("témoin : l'essai attend bien l'App");
    }

    [Fact]
    public async Task Jeu_ouvert_le_releve_ne_previent_pas()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        vm.LevierParId = null;
        var notes = new List<string>();
        vm.NotifierEssaiEnAttente = notes.Add;
        vm.JeuOuvertPourLeTest = () => true;
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);

        notes.Should().BeEmpty();
        vm.EssaiEnAttente.Should().BeTrue("témoin : l'essai attend bien l'App");
    }

    // Le relevé de fond élevé fait AVANCER l'essai (le joueur ne garde pas l'App ouverte en jouant), avec le seul
    // levier en essai : jamais l'accès général aux leviers, qui ouvrirait le retrait automatique sans fenêtre.
    // Essai-alternance.json vit sous le profil du joueur, que tout programme du compte peut écrire. L'avancée de l'essai ne
    // pose jamais un levier qui n'a pas le droit d'alterner (AutoHot), même écrit dans ce fichier.
    [Fact]
    public async Task Un_essai_ecrit_sur_un_levier_qui_n_alterne_pas_ne_pose_rien_et_s_efface()
    {
        const string horsEssai = "fixit.cpu_power_cap"; // posé au clic, jamais en alternance
        var vm = Vm(id: horsEssai);
        vm.MemoireEssai!.Ecrire(new EssaiEnAlternance(horsEssai, false, false, _maintenant, DerniereBascule: _maintenant));

        await Jouer(vm, 90);

        (_poses, _rendus).Should().Be((0, 0), "aucun levier hors alternance n'est posé sans clic");
        vm.MemoireEssai.Lire().Should().BeNull("l'essai trafiqué est effacé");
    }
}
