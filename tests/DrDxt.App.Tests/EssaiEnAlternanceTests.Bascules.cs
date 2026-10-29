using DrDxt.Tests.Shared;
using System.IO;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>Les bascules de l'essai : leviers forgés ou interdits, concurrence avec un clic, journal défaillant.</summary>
public sealed partial class EssaiEnAlternanceTests
{
    // Le même fichier forgé avec une origine « posé » : « Arrêter l'essai » rendait l'origine, donc posait le levier, au
    // moment où le joueur croyait arrêter.
    [Fact]
    public async Task Arreter_un_essai_forge_sur_un_levier_qui_n_alterne_pas_ne_pose_rien()
    {
        const string horsEssai = "fixit.cpu_power_cap";
        var vm = Vm(id: horsEssai);
        vm.MemoireEssai!.Ecrire(new EssaiEnAlternance(horsEssai, true, false, _maintenant, DerniereBascule: _maintenant));

        await vm.ArreterEssaiAsync();

        (_poses, _rendus).Should().Be((0, 0));
        vm.MemoireEssai.Lire().Should().BeNull();
    }

    // Les réglages de la carte que DrDXT n'écrit jamais (fréquence, sous-tension, ventilateur) sont « à chaud » au
    // catalogue : un fichier forgé qui les nomme ne les fait pas poser à l'arrêt.
    [Theory]
    [InlineData("gpu.core_offset")]
    [InlineData("gpu.undervolt")]
    [InlineData("gpu.fan_curve")]
    public async Task Arreter_un_essai_forge_sur_un_reglage_interdit_de_la_carte_ne_pose_rien(string interdit)
    {
        var vm = Vm(id: interdit);
        vm.MemoireEssai!.Ecrire(new EssaiEnAlternance(interdit, true, false, _maintenant, DerniereBascule: _maintenant));

        await vm.ArreterEssaiAsync();

        (_poses, _rendus).Should().Be((0, 0));
        vm.MemoireEssai.Lire().Should().BeNull();
    }

    // Une partie enchaînée aussitôt, ou un second jeu ouvert : rien ne se pose sans clic pendant qu'un jeu tourne.
    [Fact]
    public async Task Rien_ne_se_pose_sans_clic_pendant_qu_un_jeu_tourne()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        await Jouer(vm, 90);
        await Jouer(vm, 80);
        var jeuOuvert = true;
        vm.JeuOuvertPourLeTest = () => jeuOuvert;

        await Jouer(vm, 81);
        _poses.Should().Be(1, "le tour de poser tombe pendant un jeu : rien n'est posé");
        vm.MemoireEssai!.Lire()!.PoseMaintenant.Should().BeFalse();

        jeuOuvert = false;
        await Jouer(vm, 82);
        _poses.Should().Be(2, "jeu fermé, la partie suivante pose");
    }

    // Les parties d'un essai précédent du même levier ne tranchent pas le nouvel essai.
    [Fact]
    public async Task Les_parties_d_un_essai_precedent_ne_comptent_pas()
    {
        var vm = Vm();
        foreach (var (low1, pose) in new[] { (90.0, true), (80.0, false), (91.0, true), (81.0, false), (92.0, true), (79.0, false) })
        {
            _maintenant = _maintenant.AddDays(1);
            vm.MemoireParties!.Ajouter(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, low1,
                BottleneckState.CpuBoundOrAppBound, Essai: AlternanceDesEssais.Marque(Id, pose)));
        }
        await vm.DemarrerEssaiAsync(Id);

        await Jouer(vm, 90);

        vm.EssaiEnCours.Should().BeTrue("un seul jeu de parties de CET essai : rien de tranché");
    }

    // « Annuler » pendant que la fin de partie repose le levier ; l'essai ne revit pas et le levier repart.
    [Fact]
    public async Task Annuler_pendant_la_bascule_n_est_pas_defait_par_elle()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        await Jouer(vm, 90);
        await Jouer(vm, 80);   // A-B-B-A : la partie d'après celle-ci repose le levier
        var enAttente = new TaskCompletionSource<Result<TweakSnapshot>>();
        _levier.ApplyAsync().Returns(_ => { _poses++; return enAttente.Task; });

        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 80, BottleneckState.CpuBoundOrAppBound));
        var annulation = vm.ArreterEssaiSurAnnulationAsync(Id);
        annulation.IsCompleted.Should().BeTrue("l'annulation n'attend pas la bascule de ce processus : elle la laisse rendre ce qu'elle pose");
        await annulation;
        enAttente.SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, InversesDeTest.Pour(Id, "100"), "90")));
        await vm.DerniereAvanceeEssai;

        vm.MemoireEssai!.Lire().Should().BeNull("le joueur a annulé : rien ne relance l'essai");
        _rendus.Should().Be(2, "ce que la fin de partie a posé après l'annulation est rendu");
    }

    // « Arrêter l'essai » attend la bascule en cours, puis rend l'état d'origine depuis l'état réel.
    [Fact]
    public async Task Arreter_pendant_la_bascule_rend_l_etat_d_origine()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        await Jouer(vm, 90);
        await Jouer(vm, 80);   // A-B-B-A : la partie d'après celle-ci repose le levier
        var enAttente = new TaskCompletionSource<Result<TweakSnapshot>>();
        _levier.ApplyAsync().Returns(_ => { _poses++; return enAttente.Task; });

        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 80, BottleneckState.CpuBoundOrAppBound));
        var arret = vm.ArreterEssaiAsync();
        enAttente.SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, InversesDeTest.Pour(Id, "100"), "90")));
        await vm.DerniereAvanceeEssai;
        await arret;

        vm.MemoireEssai!.Lire().Should().BeNull();
        _rendus.Should().Be(2, "posé par la fin de partie, il est rendu par l'arrêt");
        vm.LigneEssai.Should().Be("Essai.Arrete");
    }

    // Levier déjà posé avant l'essai ; « Arrêter » pendant que la fin de partie le repose ne le retire pas.
    [Fact]
    public async Task Arreter_pendant_la_bascule_garde_un_levier_pose_avant_l_essai()
    {
        var vm = Vm();
        new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json"))
            .Enregistrer(new ApplyReport([new ApplyOutcome(Id, true, false, new TweakSnapshot(Id, InversesDeTest.Pour(Id, "100"), "90"), null)], null));
        await vm.DemarrerEssaiAsync(Id);
        await Jouer(vm, 90);
        _rendus.Should().Be(1);
        await Jouer(vm, 80);   // A-B-B-A : la partie d'après celle-ci repose le levier
        var enAttente = new List<TaskCompletionSource<Result<TweakSnapshot>>>();
        _levier.ApplyAsync().Returns(_ => { _poses++; var t = new TaskCompletionSource<Result<TweakSnapshot>>(); enAttente.Add(t); return t.Task; });

        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 80, BottleneckState.CpuBoundOrAppBound));
        var arret = vm.ArreterEssaiAsync();
        // La pose de l'arrêt (s'il n'attend pas) finit avant celle de la fin de partie : le pire ordre.
        for (var i = enAttente.Count - 1; i >= 0; i--) enAttente[i].SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, InversesDeTest.Pour(Id, "100"), "90")));
        await vm.DerniereAvanceeEssai;
        foreach (var t in enAttente.Where(t => !t.Task.IsCompleted)) t.SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, InversesDeTest.Pour(Id, "100"), "90")));
        await arret;

        _rendus.Should().Be(1, "l'état d'origine est « posé » : rien ne le retire");
        new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json")).Lire().Should().Contain(s => s.TweakId == Id);
    }

    // Un journal qui ne garde rien ne laisse pas un levier posé et invisible.
    [Fact]
    public async Task Un_journal_qui_ne_garde_rien_fait_repartir_le_levier()
    {
        Directory.CreateDirectory(Path.Combine(_dossier, "leviers.json"));
        var vm = Vm();

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();
        _rendus.Should().Be(1);
        vm.MemoireEssai!.Lire().Should().BeNull();
    }

    // Ni rendu ni inscrit au journal des leviers : rien ne l'annule depuis l'App, l'écran le dit sans promettre une ligne
    // qui n'existe pas.
    [Fact]
    public async Task Sans_garde_ni_journal_un_rendu_rate_dit_qu_il_reste_pose_sans_ligne_a_annuler()
    {
        Directory.CreateDirectory(Path.Combine(_dossier, "leviers.json"));
        var manifeste = ScanViewModelTestSeams.DesiredStateStore();
        manifeste.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Fail("MANIFESTE", "helper absent"));
        var vm = Vm(manifeste: manifeste);
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Fail("REFUS", "pilote"));

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        vm.LeviersPoses.Should().BeEmpty("témoin : le journal n'a rien gardé");
        vm.LigneEssai.Should().Be("Essai.ResteSansJournal");
    }

    [Fact]
    public async Task Un_journal_qui_ne_garde_rien_et_un_rendu_rate_le_disent()
    {
        Directory.CreateDirectory(Path.Combine(_dossier, "leviers.json"));
        var vm = Vm();
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Fail("REFUS", "pilote"));

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        vm.LigneEssai.Should().Be("Essai.ResteSansJournal");
    }

    // Un journal illisible ne vaut pas « déjà rendu ».
    [Fact]
    public async Task Un_journal_illisible_ne_dit_pas_l_etat_d_avant_revenu()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        File.WriteAllText(Path.Combine(_dossier, "leviers.json"), "{");

        await vm.ArreterEssaiAsync();

        vm.LigneEssai.Should().Be("Essai.ArretRate");
        vm.MemoireEssai!.Lire().Should().NotBeNull();
    }

    // Une faute pendant l'avancée arrête l'essai au lieu de le mettre en pause sur un état peut-être faux.
    [Fact]
    public async Task Une_faute_pendant_l_avancee_arrete_l_essai()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        vm.HelperJoignableSansUac = () => throw new InvalidOperationException("tuyau");

        await Jouer(vm, 90);

        vm.MemoireEssai!.Lire().Should().BeNull();
        vm.LigneEssai.Should().Be("Essai.Incertain");
    }

    // Le clic « Essayer » peut lancer le helper ; les bascules d'après, faites sans clic après chaque partie relevée, jamais
    // (SansGesteDuJoueur) : le client qui lance le helper n'y ouvre aucune invite.
    [Fact]
    public async Task Les_bascules_de_l_essai_apres_une_partie_se_font_sans_geste()
    {
        var vm = Vm();
        var portees = new List<bool>();
        _levier.ApplyAsync().Returns(_ => { portees.Add(SansGesteDuJoueur.Actif); _poses++;
            return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, InversesDeTest.Pour(Id, "100"), "90"))); });
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { portees.Add(SansGesteDuJoueur.Actif); _rendus++; return Task.FromResult(Result.Ok()); });

        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();
        portees.Should().Equal([false], "témoin : la pose du clic a le droit de lancer le helper");

        await Jouer(vm, 90);
        await Jouer(vm, 80);
        await Jouer(vm, 81);

        portees.Should().HaveCount(3).And.EndWith([true, true], "chaque bascule d'après une partie se fait sans geste");
    }
}
