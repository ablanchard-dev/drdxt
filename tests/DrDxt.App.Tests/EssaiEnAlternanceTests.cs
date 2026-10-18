using DrDxt.Tests.Shared;
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

    private ScanViewModel Vm(bool helperJoignable = true, string id = Id, IDesiredStateStore? manifeste = null, string? origine = null)
    {
        _levier.Id.Returns(id);
        _levier.RequiresElevation.Returns(true);
        _levier.ApplyAsync().Returns(_ => { _poses++; return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(id, origine ?? InversesDeTest.Pour(id, "100"), "90"))); });
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { _rendus++; return Task.FromResult(Result.Ok()); });
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k,
            journalLeviers: new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json")), manifeste: manifeste);
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
    public async Task Le_levier_suit_des_blocs_abba_puis_reste_sur_le_meilleur_etat()
    {
        var vm = Vm();
        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();
        _poses.Should().Be(1, "le clic pose le levier : la première partie l'a avec");

        await Jouer(vm, 90);
        _rendus.Should().Be(1, "après une partie avec, la suivante est sans");
        await Jouer(vm, 80);
        _poses.Should().Be(1, "A-B-B-A : deux parties sans de suite");
        await Jouer(vm, 81);
        _poses.Should().Be(2);
        // Avec : 91, 92, 93, 94 ; sans : 79, 82, 80.5, puis la sixième.
        foreach (var low1 in new[] { 91, 92, 79, 82, 93, 94, 80.5 }) await Jouer(vm, low1);
        vm.EssaiEnCours.Should().BeTrue("cinq parties sans : le test à blanc en demande six");
        await Jouer(vm, 78);

        vm.EssaiEnCours.Should().BeFalse("test à blanc calme, puis avec nettement au-dessus : tranché");
        vm.LigneEssai.Should().Be("Essai.Fini.Mieux");
        vm.MemoireParties!.Lire().Select(p => p.Essai).Should().OnlyContain(e => e != null && e.StartsWith(Id + ":", StringComparison.Ordinal));
        (_poses - _rendus).Should().Be(1, "mieux en jeu : il reste posé");
    }

    // La désinstallation ne rend que ce que le manifeste signé porte : posé hors de lui, le levier de l'essai lui survivait.
    [Fact]
    public async Task La_pose_de_l_essai_inscrit_de_quoi_la_rendre_a_la_desinstallation()
    {
        var manifeste = ScanViewModelTestSeams.DesiredStateStore();
        (await Vm(manifeste: manifeste).DemarrerEssaiAsync(Id)).Should().BeTrue();

        await manifeste.Received(1).CommitAsync(Arg.Is<DesiredState>(d => d.Tweaks.Count == 0
            && d.RevertOnly!.Count == 1 && d.RevertOnly[0].TweakId == Id
            && d.RevertOnly[0].OriginalValueJson == InversesDeTest.Pour(Id, "100")));
    }

    // Pour l'Optimiser, la limite de latence part au service qui la réapplique ; pendant un essai, il la reposerait
    // pendant la partie « sans ». La garde de l'essai ne fait que la rendre à la désinstallation.
    [Fact]
    public async Task Un_levier_que_le_service_reapplique_n_est_garde_que_pour_la_desinstallation()
    {
        var manifeste = ScanViewModelTestSeams.DesiredStateStore();
        (await Vm(manifeste: manifeste, id: "latency.reflex").DemarrerEssaiAsync("latency.reflex")).Should().BeTrue();

        await manifeste.Received(1).CommitAsync(Arg.Is<DesiredState>(d => d.Tweaks.Count == 0
            && d.RevertOnly!.Count == 1 && d.RevertOnly[0].TweakId == "latency.reflex"));
    }

    // Rendu, le levier est à l'origine : sa garde sort du manifeste, la désinstallation n'a plus rien à rendre pour lui.
    [Fact]
    public async Task Chaque_rendu_de_l_essai_retire_sa_garde()
    {
        var retraits = new List<string>();
        var vm = Vm();
        vm.RetirerDuManifeste = id => { retraits.Add(id); return Task.FromResult(Result.Ok()); };
        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();
        retraits.Should().BeEmpty("témoin : posé, la garde reste");

        await Jouer(vm, 90);

        retraits.Should().Equal(Id);
    }

    // Une sous-tension AMD peut survivre au redémarrage et la désinstallation ne sait pas la rendre : jamais essayée.
    [Fact]
    public async Task Un_levier_que_la_desinstallation_ne_rendrait_pas_n_est_jamais_essaye()
    {
        var manifeste = ScanViewModelTestSeams.DesiredStateStore();
        var adlx = PrivilegedOpCodec.Serialize(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 1100)).Value!;
        var vm = Vm(id: "gpu.undervolt", manifeste: manifeste, origine: adlx);

        (await vm.DemarrerEssaiAsync("gpu.undervolt")).Should().BeFalse();

        (_poses, _rendus).Should().Be((1, 1), "posé, puis rendu aussitôt");
        vm.EssaiEnCours.Should().BeFalse();
        vm.LigneEssai.Should().Be("Essai.PasRendable");
        await manifeste.DidNotReceive().CommitAsync(Arg.Any<DesiredState>());
    }

    // Témoin : la même sous-tension sur NVIDIA est un décalage du cœur, volatil, que la désinstallation remet à 0.
    [Fact]
    public async Task Un_decalage_nvidia_volatil_rendu_a_la_desinstallation_s_essaie()
    {
        (await Vm(id: "gpu.undervolt").DemarrerEssaiAsync("gpu.undervolt")).Should().BeTrue();
        _rendus.Should().Be(0);
    }

    // La pose de l'essai s'acquitte une fois, quand le journal des leviers porte son origine ; rendue aussitôt faute de garde,
    // elle n'a rien à acquitter (le helper trouvera la valeur d'origine en place).
    private List<bool> AcquitsALaPose(string id = Id)
    {
        var acquits = new List<bool>();
        _levier.ApplyAsync().Returns(_ =>
        {
            _poses++;
            PorteeDePose.Courante?.Noter(() =>
            {
                acquits.Add(new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json")).Lire().Any(s => s.TweakId == id));
                return Task.FromResult(Result.Ok());
            });
            return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(id, InversesDeTest.Pour(id, "100"), "90")));
        });
        return acquits;
    }

    [Fact]
    public async Task La_pose_de_l_essai_s_acquitte_une_fois_apres_que_le_journal_porte_l_origine()
    {
        var vm = Vm();
        var acquits = AcquitsALaPose();

        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();

        acquits.Should().Equal(true);
    }

    [Fact]
    public async Task Sans_garde_inscrite_la_pose_rendue_n_acquitte_rien()
    {
        var manifeste = ScanViewModelTestSeams.DesiredStateStore();
        manifeste.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Fail("MANIFESTE", "helper absent"));
        var vm = Vm(manifeste: manifeste);
        var acquits = AcquitsALaPose();

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        _rendus.Should().Be(1, "posé, puis rendu aussitôt");
        acquits.Should().HaveCount(0);
    }

    // Resté posé faute de rendu : son origine entre au journal des leviers, l'acquit part après.
    [Fact]
    public async Task Sans_garde_inscrite_un_rendu_rate_s_acquitte_apres_que_le_journal_porte_l_origine()
    {
        var manifeste = ScanViewModelTestSeams.DesiredStateStore();
        manifeste.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Fail("MANIFESTE", "helper absent"));
        var vm = Vm(manifeste: manifeste);
        var acquits = AcquitsALaPose();
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Fail("REFUS", "pilote"));

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        acquits.Should().Equal(true);
    }

    // Helper absent, manifeste illisible : sans garde inscrite, une désinstallation laisserait le levier posé.
    [Fact]
    public async Task Sans_garde_inscrite_l_essai_ne_garde_rien_de_pose_et_le_dit()
    {
        var manifeste = ScanViewModelTestSeams.DesiredStateStore();
        manifeste.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Fail("MANIFESTE", "helper absent"));
        var vm = Vm(manifeste: manifeste);

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        (_poses, _rendus).Should().Be((1, 1), "posé, puis rendu aussitôt");
        vm.EssaiEnCours.Should().BeFalse();
        vm.LeviersPoses.Should().BeEmpty("rien n'est gardé au journal");
        vm.LigneEssai.Should().Be("Essai.PasInscrit");
    }

    // Ni inscrit ni rendu : le levier reste posé. Il reste alors au journal, visible et annulable, et l'écran le dit.
    [Fact]
    public async Task Sans_garde_inscrite_un_rendu_rate_garde_le_levier_visible_et_le_dit()
    {
        var manifeste = ScanViewModelTestSeams.DesiredStateStore();
        manifeste.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Fail("MANIFESTE", "helper absent"));
        var vm = Vm(manifeste: manifeste);
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Fail("REFUS", "pilote"));

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        vm.EssaiEnCours.Should().BeFalse();
        vm.LeviersPoses.Should().ContainSingle(l => l.Id == Id);
        vm.LigneEssai.Should().Be("Essai.PasInscritResteJournal");
    }

    [Fact]
    public async Task Une_bascule_sans_garde_inscrite_rend_aussitot_et_dit_pourquoi()
    {
        var manifeste = ScanViewModelTestSeams.DesiredStateStore();
        var vm = Vm(manifeste: manifeste);
        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();
        await Jouer(vm, 90);
        await Jouer(vm, 80);
        manifeste.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Fail("MANIFESTE", "illisible"));

        await Jouer(vm, 81);

        (_poses - _rendus).Should().Be(0, "A-B-B-A voulait poser ; sans garde, rien ne reste");
        vm.MemoireEssai!.Lire()!.EnPause.Should().BeTrue();
        vm.LigneEssai.Should().Be("Essai.PasInscrit");
    }

    // Un levier de latence que l'instrument n'a mesuré sur aucune partie se juge sur les images : le verdict le dit.
    [Fact]
    public async Task Le_verdict_d_un_levier_de_latence_juge_sur_les_images_le_dit()
    {
        var vm = Vm(id: "latency.reflex");
        (await vm.DemarrerEssaiAsync("latency.reflex")).Should().BeTrue();

        foreach (var low1 in new[] { 90, 80, 81, 91, 92, 79, 82, 93, 94, 80.5, 78 }) await Jouer(vm, low1);

        vm.EssaiEnCours.Should().BeFalse();
        vm.LigneEssai.Should().Be("Essai.Fini.Mieux Essai.JugeSurLesImages");
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
        _poses.Should().Be(1, "A-B-B-A : le rang suivant est sans, rien à reposer");
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
        vm.ArreterEssaiSurAnnulation(Id);
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
