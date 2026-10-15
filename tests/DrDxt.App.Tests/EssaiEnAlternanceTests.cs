using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Un levier à chaud essayé une partie sur deux, sans geste à chaque partie. Le vrai
/// ViewModel, un levier factice, de vrais fichiers temporaires : rien ne touche la machine.
/// </summary>
public sealed class EssaiEnAlternanceTests : IDisposable
{
    private const string Id = "gpu.power_limit";
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-essai-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private readonly ITweak _levier = Substitute.For<ITweak>();
    private int _poses, _rendus;
    private DateTimeOffset _maintenant = new(2026, 10, 7, 20, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _demarrage = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    private ScanViewModel Vm(bool helperJoignable = true, string id = Id)
    {
        _levier.Id.Returns(id);
        _levier.RequiresElevation.Returns(true);
        _levier.ApplyAsync().Returns(_ => { _poses++; return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(id, "100", "90"))); });
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { _rendus++; return Task.FromResult(Result.Ok()); });
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k,
            journalLeviers: new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json")));
        vm.MemoireParties = new PartiesMemorisees(Path.Combine(_dossier, "choix.json"), Path.Combine(_dossier, "parties.json"));
        vm.MemoireEssai = new MemoireDeLEssai(Path.Combine(_dossier, "essai.json"));
        vm.LevierParId = demande => demande == id ? _levier : null;
        vm.HelperJoignableSansUac = () => Task.FromResult(helperJoignable);
        vm.Horloge = () => _maintenant;
        vm.DemarrageDuPc = () => _demarrage;
        return vm;
    }

    private async Task Jouer(ScanViewModel vm, double low1)
    {
        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, low1,
            BottleneckState.CpuBoundOrAppBound));
        await vm.DerniereAvanceeEssai;
    }

    [Fact]
    public async Task Le_levier_alterne_une_partie_sur_deux_puis_reste_sur_le_meilleur_etat()
    {
        var vm = Vm();
        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();
        _poses.Should().Be(1, "le clic pose le levier : la première partie l'a avec");

        await Jouer(vm, 90);
        _rendus.Should().Be(1, "après une partie avec, la suivante est sans");
        await Jouer(vm, 80);
        _poses.Should().Be(2);
        await Jouer(vm, 91);
        await Jouer(vm, 81);
        await Jouer(vm, 92);
        vm.EssaiEnCours.Should().BeTrue("2 contre 3 : pas encore tranché");
        await Jouer(vm, 79);

        vm.EssaiEnCours.Should().BeFalse("3 contre 3 nets : tranché");
        vm.LigneEssai.Should().Be("Essai.Fini.Mieux");
        vm.MemoireParties!.Lire().Select(p => p.Essai).Should().OnlyContain(e => e != null && e.StartsWith(Id + ":", StringComparison.Ordinal));
        (_poses - _rendus).Should().Be(1, "mieux en jeu : il reste posé");
    }

    // La pose de l'essai se date, comme celles d'Optimiser : la garde d'incident la couvre.
    [Fact]
    public async Task La_pose_de_l_essai_se_date_pour_la_garde()
    {
        var vm = Vm();
        vm.JournalChangements = new JournalDesChangements(Path.Combine(_dossier, "changements.json"));
        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();
        vm.JournalChangements.Lire().Should().Contain(c => c.Cle == "pose:" + Id && c.Quand == _maintenant);
    }

    // DrDXT relancé pendant un essai : sans scan ni fin de partie, l'essai doit quand même se dire, et son
    // bouton d'arrêt ne doit pas rester caché dans la zone de résultats.
    [Fact]
    public async Task Un_essai_en_cours_se_dit_des_le_lancement_et_s_arrete_sans_scan()
    {
        (await Vm().DemarrerEssaiAsync(Id)).Should().BeTrue();

        var relance = Vm();
        var (poses, rendus) = (_poses, _rendus);   // re-régler le substitut rappelle ses anciens rappels
        relance.LigneEssai.Should().BeNull("témoin : rien n'est dit avant le chargement");
        relance.ChargerParties();
        relance.LigneEssai.Should().Be("Essai.EnCours");
        relance.EssaiEnCours.Should().BeTrue();

        await relance.ArreterEssaiCommand.ExecuteAsync(null);
        relance.EssaiEnCours.Should().BeFalse();
        (_rendus - rendus).Should().Be(1, "l'arrêt rend l'état d'origine");
        (_poses - poses).Should().Be(0);
        relance.LigneEssai.Should().Be("Essai.Arrete");
    }

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
        // En pause, rien n'a été touché : l'état (posé) est connu, la partie compte avec lui.
        vm.MemoireParties!.Lire()[0].Essai.Should().Be(AlternanceDesEssais.Marque(Id, true));
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
    // VOLATIL (limite de la carte, PL1) s'efface au redémarrage ; son état est alors connu sans rien écrire : « sans ». L'essai
    // continue, les parties d'après le démarrage comptent côté « sans » ; sinon un joueur qui éteint le soir ne finit jamais.
    [Fact]
    public async Task Un_redemarrage_rend_un_reglage_volatil_a_l_origine_et_l_essai_continue()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        _poses.Should().Be(1);
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);

        vm.EssaiEnCours.Should().BeTrue("le redémarrage n'arrête plus l'essai d'un réglage volatil");
        vm.MemoireParties!.Lire()[0].Essai.Should().Be(AlternanceDesEssais.Marque(Id, false), "jouée après le démarrage : sans le réglage");
        _rendus.Should().Be(0, "rien à rendre : le redémarrage l'a déjà fait");
        _poses.Should().Be(2, "après une partie sans, la suivante est avec");
    }

    // Le cas réel : le relevé de fond (aucun levier accessible) enregistre la partie. Il ne pose rien, mais il note l'état vrai.
    [Fact]
    public async Task Le_releve_de_fond_note_un_reglage_volatil_efface_sans_rien_poser()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        vm.LevierParId = null; // le relevé de fond
        _demarrage = _maintenant.AddHours(2);

        await Jouer(vm, 90);

        vm.MemoireEssai!.Lire()!.PoseMaintenant.Should().BeFalse("le redémarrage l'a effacé : l'essai ne reste plus figé sur « posé »");
        vm.MemoireParties!.Lire()[0].Essai.Should().Be(AlternanceDesEssais.Marque(Id, false));
        (_poses, _rendus).Should().Be((1, 0), "le relevé de fond ne touche à aucun levier");
    }

    // Le relevé de fond élevé fait AVANCER l'essai (le joueur ne garde pas l'App ouverte en jouant), avec le seul
    // levier en essai : jamais l'accès général aux leviers, qui ouvrirait le retrait automatique sans fenêtre.
    [Fact]
    public async Task Le_releve_de_fond_eleve_fait_avancer_l_essai_avec_le_seul_levier_en_essai()
    {
        var vm = Vm();
        await vm.DemarrerEssaiAsync(Id);
        var demandes = new List<string>();
        vm.LevierParId = null;
        vm.LevierDeLEssai = id => { demandes.Add(id); return id == Id ? _levier : null; };

        await Jouer(vm, 90);

        _rendus.Should().Be(1, "après une partie avec, la suivante est sans : rendu en fond");
        vm.MemoireEssai!.Lire()!.PoseMaintenant.Should().BeFalse();
        demandes.Should().OnlyContain(id => id == Id);
    }

    // Essai-alternance.json vit sous le profil du joueur, que tout programme du compte peut écrire. Le
    // relevé de fond élevé ne pose jamais un levier qui n'a pas le droit d'alterner (AutoHot), même écrit dans ce fichier.
    [Fact]
    public async Task Un_essai_ecrit_sur_un_levier_qui_n_alterne_pas_ne_pose_rien_et_s_efface()
    {
        const string horsEssai = "fixit.cpu_power_cap"; // posé au clic, jamais en alternance
        var vm = Vm(id: horsEssai);
        vm.MemoireEssai!.Ecrire(new EssaiEnAlternance(horsEssai, false, false, _maintenant, DerniereBascule: _maintenant));
        vm.LevierParId = null;
        vm.LevierDeLEssai = _ => _levier;

        await Jouer(vm, 90);

        (_poses, _rendus).Should().Be((0, 0), "aucun levier hors alternance n'est posé en fond");
        vm.MemoireEssai.Lire().Should().BeNull("l'essai trafiqué est effacé");
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
        var enAttente = new TaskCompletionSource<Result<TweakSnapshot>>();
        _levier.ApplyAsync().Returns(_ => { _poses++; return enAttente.Task; });

        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 80, BottleneckState.CpuBoundOrAppBound));
        vm.ArreterEssaiSurAnnulation(Id);
        enAttente.SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, "100", "90")));
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
        var enAttente = new TaskCompletionSource<Result<TweakSnapshot>>();
        _levier.ApplyAsync().Returns(_ => { _poses++; return enAttente.Task; });

        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 80, BottleneckState.CpuBoundOrAppBound));
        var arret = vm.ArreterEssaiAsync();
        enAttente.SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, "100", "90")));
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
            .Enregistrer(new ApplyReport([new ApplyOutcome(Id, true, false, new TweakSnapshot(Id, "100", "90"), null)], null));
        await vm.DemarrerEssaiAsync(Id);
        await Jouer(vm, 90);
        _rendus.Should().Be(1);
        var enAttente = new List<TaskCompletionSource<Result<TweakSnapshot>>>();
        _levier.ApplyAsync().Returns(_ => { _poses++; var t = new TaskCompletionSource<Result<TweakSnapshot>>(); enAttente.Add(t); return t.Task; });

        _maintenant = _maintenant.AddDays(1);
        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 80, BottleneckState.CpuBoundOrAppBound));
        var arret = vm.ArreterEssaiAsync();
        // La pose de l'arrêt (s'il n'attend pas) finit avant celle de la fin de partie : le pire ordre.
        for (var i = enAttente.Count - 1; i >= 0; i--) enAttente[i].SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, "100", "90")));
        await vm.DerniereAvanceeEssai;
        foreach (var t in enAttente.Where(t => !t.Task.IsCompleted)) t.SetResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, "100", "90")));
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
}
