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
    private string CheminDuVerrou => Path.Combine(_dossier, "essai.verrou");
    private int _poses, _rendus;
    private DateTimeOffset _maintenant = new(2026, 10, 7, 20, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _demarrage = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    private ScanViewModel Vm(bool helperJoignable = true, string id = Id, IDesiredStateStore? manifeste = null, string? origine = null,
        ISafetyManager? safety = null, DrDxt.Application.Tweaks.Hardware.IRegistryReader? registre = null, string? posee = null)
    {
        _levier.Id.Returns(id);
        _levier.RequiresElevation.Returns(true);
        _levier.ApplyAsync().Returns(_ => { _poses++; return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(id, origine ?? InversesDeTest.Pour(id, "100"), posee ?? "90"))); });
        _levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { _rendus++; return Task.FromResult(Result.Ok()); });
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k,
            journalLeviers: new JournalDesLeviersAppliques(Path.Combine(_dossier, "leviers.json")), manifeste: manifeste, safety: safety,
            registreGains: registre);
        vm.MemoireParties = new PartiesMemorisees(Path.Combine(_dossier, "choix.json"), Path.Combine(_dossier, "parties.json"));
        vm.MemoireEssai = new MemoireDeLEssai(Path.Combine(_dossier, "essai.json"));
        vm.PrendreVerrouEssai = () => VerrouDuReleve.Prendre(CheminDuVerrou);
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

    // Une pose que la machine ne confirme pas ne lance pas d'essai : les parties « avec » mesureraient autre chose que le
    // levier. Rendue tout de suite, et le dire.
    [Theory]
    [InlineData(EtatApplique.NotApplied)]
    [InlineData(EtatApplique.Drifted)]
    [InlineData(EtatApplique.Unavailable)]
    [InlineData(EtatApplique.Unknown)]
    public async Task Une_pose_non_confirmee_ne_lance_pas_l_essai_et_repart(EtatApplique verdict)
    {
        var vm = Vm();
        vm.VerifierLaPose = _ => verdict;

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        _poses.Should().Be(1);
        _rendus.Should().Be(1, "rendue aussitôt : rien ne reste posé sans essai");
        vm.MemoireEssai!.Lire().Should().BeNull();
        vm.LigneEssai.Should().Contain("Essai.PoseNonRelue");
    }

    // Rendu accepté mais pas relu à l'origine : la partie « sans » mesurerait encore le levier. L'essai se met en pause, le
    // levier reste au journal (et à « Annuler »), et c'est dit.
    [Theory]
    [InlineData(EtatApplique.Applied)]
    [InlineData(EtatApplique.Drifted)]
    [InlineData(EtatApplique.Unknown)]
    public async Task Un_rendu_de_l_essai_non_relu_a_l_origine_met_l_essai_en_pause(EtatApplique relu)
    {
        var vm = Vm();
        vm.VerifierLaPose = _ => _rendus == 0 ? EtatApplique.Applied : relu;
        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();

        await Jouer(vm, 90);

        _rendus.Should().Be(1);
        vm.MemoireEssai!.Lire()!.EnPause.Should().BeTrue();
        vm.LigneEssai.Should().Contain("Essai.RenduNonRelu");
        vm.LeviersPoses.Should().ContainSingle("rien ne dit qu'il est rendu : il reste à annuler");
    }

    // Une bascule notée pendant que l'horloge avançait, puis l'heure remise : datée dans le futur, aucune partie ne commençait
    // après elle, et l'essai restait « en cours » pour toujours, levier posé. À la lecture, rien n'est plus tard que maintenant.
    [Fact]
    public void Une_bascule_datee_dans_le_futur_est_ramenee_a_maintenant()
    {
        var futur = _maintenant.AddDays(30);
        var chemin = Path.Combine(_dossier, "essai.json");
        new MemoireDeLEssai(chemin).Ecrire(new EssaiEnAlternance(Id, false, true, futur, DerniereBascule: futur)).IsOk.Should().BeTrue();

        var lu = new MemoireDeLEssai(chemin, () => _maintenant).Lire()!;

        lu.Debut.Should().Be(_maintenant);
        lu.Depuis.Should().Be(_maintenant);
    }

    [Fact]
    public async Task Un_rendu_de_l_essai_relu_a_l_origine_continue_l_essai()
    {
        var vm = Vm();
        vm.VerifierLaPose = _ => _rendus == 0 ? EtatApplique.Applied : EtatApplique.NotApplied;
        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();

        await Jouer(vm, 90);

        vm.MemoireEssai!.Lire()!.EnPause.Should().BeFalse();
        vm.LeviersPoses.Should().BeEmpty();
    }

    // Une partie affichée mais non écrite serait perdue au relancement, sans un mot : la carte le dit en tête.
    [Fact]
    public void Une_partie_non_gardee_sur_le_disque_se_dit()
    {
        var vm = Vm();
        var fichier = Path.Combine(_dossier, "pas-un-dossier");
        File.WriteAllText(fichier, "");
        vm.MemoireParties = new PartiesMemorisees(Path.Combine(_dossier, "choix.json"), Path.Combine(fichier, "parties.json"));

        vm.EnregistrerPartie(new PartieRelevee("cod.exe", _maintenant, _maintenant.AddMinutes(30), 4, 130, 90, BottleneckState.CpuBoundOrAppBound));

        vm.LignesParties.Should().NotBeEmpty();
        vm.LignesParties[0].Should().Be("Parties.NonGardee");
    }

    // « DrDXT ne change rien sans point de restauration » : l'essai non plus. Refusé, rien n'est posé, et la ligne le dit.
    [Fact]
    public async Task Sans_point_de_restauration_l_essai_ne_pose_rien()
    {
        var safety = Substitute.For<ISafetyManager>();
        safety.EnsureRestorePointAsync().Returns(Result.Fail(ErrorCodes.RestoreDeclined, "refusé"));
        var vm = Vm(safety: safety);

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        _poses.Should().Be(0);
        vm.MemoireEssai!.Lire().Should().BeNull();
        vm.LigneEssai.Should().Be("Apply.RestoreRequired.Declined");
    }

    // La création du point peut durer (WMI, VSS) : le relevé de fond, qui écrit l'essai sous le même verrou, ne l'attend pas.
    [Fact]
    public async Task Le_point_de_restauration_se_cree_sans_tenir_le_verrou_de_l_essai()
    {
        var verrouLibre = new List<bool>();
        var safety = Substitute.For<ISafetyManager>();
        safety.EnsureRestorePointAsync().Returns(_ =>
        {
            using var autre = VerrouDuReleve.Prendre(CheminDuVerrou);
            verrouLibre.Add(autre is not null);
            return Task.FromResult(Result.Ok());
        });

        (await Vm(safety: safety).DemarrerEssaiAsync(Id)).Should().BeTrue();

        verrouLibre.Should().Equal(true);
    }

    [Fact]
    public async Task Une_restauration_interdite_par_strategie_se_dit()
    {
        var safety = Substitute.For<ISafetyManager>();
        safety.EnsureRestorePointAsync().Returns(Result.Fail(ErrorCodes.RestorePolicy, "politique"));
        var vm = Vm(safety: safety);

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        vm.LigneEssai.Should().Be("Apply.RestoreRequired.Policy");
    }

    [Fact]
    public async Task L_essai_passe_par_la_porte_du_point_de_restauration()
    {
        var safety = ScanViewModelTestSeams.Safety();
        (await Vm(safety: safety).DemarrerEssaiAsync(Id)).Should().BeTrue();

        await safety.Received(1).EnsureRestorePointAsync();
    }

    [Fact]
    public async Task Une_pose_relue_en_place_lance_l_essai()
    {
        var vm = Vm();
        vm.VerifierLaPose = _ => EtatApplique.Applied;

        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();
        _rendus.Should().Be(0);
    }

    // Trois jeux joués à tour de rôle : le jeu jugé n'avait jamais 9 parties d'essai dans l'anneau des 20 dernières, tous jeux
    // confondus, et le rang A-B-B-A se décalait à chaque partie évincée : le levier basculait sans fin, l'essai restait « en
    // cours ». Les parties d'essai se gardent à part, et l'essai tranche.
    [Fact]
    public async Task Un_essai_tranche_meme_quand_le_joueur_alterne_trois_jeux()
    {
        var vm = Vm();
        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();
        string[] jeux = ["cod.exe", "valorant.exe", "apex.exe"];

        for (var i = 0; i < 60 && vm.EssaiEnCours; i++)
        {
            _maintenant = _maintenant.AddHours(6);
            var pose = _poses > _rendus;
            vm.EnregistrerPartie(new PartieRelevee(jeux[i % 3], _maintenant, _maintenant.AddMinutes(30), 4, 130,
                pose ? 95 : 80, BottleneckState.CpuBoundOrAppBound));
            await vm.DerniereAvanceeEssai;
        }

        vm.EssaiEnCours.Should().BeFalse("l'essai tranche au lieu de basculer sans fin");
        vm.LigneEssai.Should().Be("Essai.Fini.Mieux");
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

    // Une sous-tension de la carte ne s'essaie jamais : Optimiser ne la propose pas, et l'essai la refuse avant toute pose.
    [Fact]
    public async Task Une_sous_tension_de_la_carte_n_est_jamais_essayee()
    {
        var manifeste = ScanViewModelTestSeams.DesiredStateStore();
        var adlx = PrivilegedOpCodec.Serialize(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 1100)).Value!;
        var vm = Vm(id: "gpu.undervolt", manifeste: manifeste, origine: adlx);

        (await vm.DemarrerEssaiAsync("gpu.undervolt")).Should().BeFalse();

        (_poses, _rendus).Should().Be((0, 0), "refusée avant toute pose");
        vm.EssaiEnCours.Should().BeFalse();
        await manifeste.DidNotReceive().CommitAsync(Arg.Any<DesiredState>());
    }

    // Témoin : un réglage de la carte qu'Optimiser propose et que la désinstallation sait rendre s'essaie.
    [Fact]
    public async Task Une_limite_de_puissance_de_la_carte_s_essaie()
    {
        (await Vm(id: "gpu.power_limit").DemarrerEssaiAsync("gpu.power_limit")).Should().BeTrue();
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

    // Une origine que la désinstallation ne saurait pas rendre (illisible, ou d'une forme que le manifeste ne porte pas) :
    // l'essai ne garde rien de posé, il rend tout de suite et le dit. C'est la clôture d'un levier à chaud futur ou d'une
    // origine abîmée, pas une branche morte.
    [Fact]
    public async Task Une_origine_que_la_desinstallation_ne_rendrait_pas_n_est_pas_essayee()
    {
        var vm = Vm(origine: "illisible");

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        (_poses, _rendus).Should().Be((1, 1), "posé, puis rendu aussitôt");
        vm.EssaiEnCours.Should().BeFalse();
        vm.LeviersPoses.Should().BeEmpty();
        vm.LigneEssai.Should().Be("Essai.PasRendable");
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

    // Le relevé de fond (App.ReleverEnFond) : mêmes fichiers, aucun levier, aucun helper. Il ne pose ni ne rend rien.
    private ScanViewModel ReleveDeFond(ScanViewModel app)
    {
        var fond = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);
        fond.MemoireParties = app.MemoireParties;
        fond.MemoireEssai = app.MemoireEssai;
        fond.PrendreVerrouEssai = app.PrendreVerrouEssai;
        fond.Horloge = () => _maintenant;
        fond.DemarrageDuPc = () => _demarrage;
        return fond;
    }

    // Le PC sort de veille le matin (Kernel-Boot 27 au réveil, un levier volatil est rendu), puis une partie est relevée.
    private async Task JouerApresUnReveil(ScanViewModel releveur)
    {
        _demarrage = _maintenant.AddDays(1).AddMinutes(-3);
        _maintenant = _maintenant.AddDays(1);
        releveur.EnregistrerPartie(new PartieRelevee("JeuBClient-Win64-Shipping.exe", _maintenant, _maintenant.AddMinutes(30), 4,
            130, 80, BottleneckState.CpuBoundOrAppBound));
        await releveur.DerniereAvanceeEssai;
    }

    // Un réveil rend la limite et le relevé de fond ne la repose pas : les parties suivantes, jouées « sans » là où les
    // blocs les voulaient « avec », comptaient dans les blocs. Jouées hors du plan, elles
    // ne comptent plus, et l'App ouverte dit quoi faire, avec le nom du jeu.
    [Fact]
    public async Task Une_partie_jouee_hors_du_plan_ne_compte_pas_et_l_App_dit_quoi_faire()
    {
        var app = Vm();
        (await app.DemarrerEssaiAsync(Id)).Should().BeTrue();
        var fond = ReleveDeFond(app);

        for (var jour = 0; jour < 4; jour++) await JouerApresUnReveil(fond);

        app.MemoireParties!.Lire().Should().HaveCount(4).And.OnlyContain(p => p.Essai == null, "aucune n'avait le levier que les blocs voulaient");
        _poses.Should().Be(1, "témoin : rien ne s'est posé sans fenêtre");
        app.ChargerParties();
        app.EssaiEnAttente.Should().BeTrue();
        app.LigneEssai.Should().Be("Essai.EnAttente");
    }

    // « Reprendre l'essai » est un clic : il pose le levier (l'invite Windows éventuelle vient de lui), et la partie suivante
    // compte avec.
    [Fact]
    public async Task Reprendre_l_essai_pose_le_levier_et_la_partie_suivante_compte()
    {
        var app = Vm(helperJoignable: false);
        (await app.DemarrerEssaiAsync(Id)).Should().BeTrue();
        await JouerApresUnReveil(ReleveDeFond(app));
        app.ChargerParties();
        app.EssaiEnAttente.Should().BeTrue("témoin");

        await app.ReprendreEssaiCommand.ExecuteAsync(null);

        _poses.Should().Be(2, "le clic pose, même sans helper déjà lancé : c'est lui qui porte l'invite");
        app.EssaiEnAttente.Should().BeFalse();
        await Jouer(app, 90);
        app.MemoireParties!.Lire().OrderBy(p => p.Debut).Last().Essai.Should().Be(AlternanceDesEssais.Marque(Id, true));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void La_ligne_d_attente_nomme_le_jeu_et_le_geste(string langue)
    {
        var dire = new DrDxt.Infrastructure.Localization.ResxLocalizer();
        var culture = new System.Globalization.CultureInfo(langue);
        var ligne = string.Format(culture, dire.Get("Essai.EnAttente", culture), "Limite de puissance", "JeuB");

        ligne.Should().Contain("JeuB").And.Contain("DrDXT").And.Contain(dire.Get("Essai.Reprendre", culture));
        dire.Get("Essai.TonJeu", culture).Should().NotBe("Essai.TonJeu");
    }

    // L'App et le relevé de fond tournent ensemble et écrivent le même essai. Pendant qu'un processus tient le verrou de
    // l'essai (il pose ou rend), l'autre n'écrit rien et ne marque pas la partie : écrite par-dessus, la pose était perdue
    // (fichier « rendu », levier posé, et « Arrêter l'essai » ne rendait plus rien).
    [Fact]
    public async Task Pendant_que_l_autre_processus_tient_l_essai_le_releve_de_fond_n_ecrit_ni_ne_marque_rien()
    {
        var app = Vm();
        (await app.DemarrerEssaiAsync(Id)).Should().BeTrue();
        var fond = ReleveDeFond(app);
        var fichier = Path.Combine(_dossier, "essai.json");
        var avant = File.ReadAllText(fichier);

        using (VerrouDuReleve.Prendre(CheminDuVerrou)) await JouerApresUnReveil(fond);

        File.ReadAllText(fichier).Should().Be(avant);
        app.MemoireParties!.Lire().Should().ContainSingle().Which.Essai.Should().BeNull();
        await JouerApresUnReveil(fond);
        File.ReadAllText(fichier).Should().NotBe(avant, "témoin : verrou libre, le réveil se note");
    }

    [Fact]
    public async Task Reprendre_pendant_que_l_autre_processus_tient_l_essai_ne_pose_rien_et_le_dit()
    {
        var app = Vm(helperJoignable: false);
        (await app.DemarrerEssaiAsync(Id)).Should().BeTrue();
        await JouerApresUnReveil(ReleveDeFond(app));
        app.AttenteDuVerrouEssai = TimeSpan.Zero;

        using (VerrouDuReleve.Prendre(CheminDuVerrou)) await app.ReprendreEssaiCommand.ExecuteAsync(null);

        _poses.Should().Be(1, "rien ne se pose pendant que l'autre processus change l'essai");
        app.LigneEssai.Should().Be("Essai.Occupe");
    }

    // Rien ne se pose pendant une partie : le clic le dit au lieu de ne rien faire.
    [Fact]
    public async Task Reprendre_pendant_une_partie_dit_de_fermer_le_jeu()
    {
        var app = Vm(helperJoignable: false);
        (await app.DemarrerEssaiAsync(Id)).Should().BeTrue();
        await JouerApresUnReveil(ReleveDeFond(app));
        app.JeuOuvertPourLeTest = () => true;

        await app.ReprendreEssaiCommand.ExecuteAsync(null);

        _poses.Should().Be(1);
        app.LigneEssai.Should().Be("Essai.FermeLeJeu");
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Les_lignes_du_clic_qui_attend_disent_quoi_faire(string langue)
    {
        var dire = new DrDxt.Infrastructure.Localization.ResxLocalizer();
        var culture = new System.Globalization.CultureInfo(langue);

        string.Format(culture, dire.Get("Essai.FermeLeJeu", culture), "Limite de puissance", "JeuB")
            .Should().Contain("JeuB").And.Contain(dire.Get("Essai.Reprendre", culture));
        string.Format(culture, dire.Get("Essai.Occupe", culture), "Limite de puissance")
            .Should().Contain(dire.Get("Essai.Reprendre", culture));
    }

    // La mémoire de l'essai ne se lit pas (tenue par un autre programme) : ce n'est pas « aucun essai ». Un essai neuf
    // écraserait l'origine de celui en cours ; il ne part pas, et rien n'est posé.
    [Fact]
    public async Task Une_memoire_d_essai_illisible_n_est_pas_aucun_essai()
    {
        var vm = Vm();
        var chemin = Path.Combine(_dossier, "essai.json");
        vm.MemoireEssai!.Ecrire(new EssaiEnAlternance(Id, true, false, _maintenant)).IsOk.Should().BeTrue();

        using (new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.None))
            (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        vm.LigneEssai.Should().Contain("Essai.DejaEnCours", "le clic ne reste pas muet");
        _poses.Should().Be(0, "rien n'est posé sur un essai qu'on ne sait pas lire");
        _rendus.Should().Be(0);
        vm.MemoireEssai.Lire()!.PoseALOrigine.Should().BeTrue("l'origine de l'essai en cours reste");
    }

    // Abîmée, la mémoire de l'essai est mise de côté avant qu'un essai neuf la remplace : l'origine perdue s'y lit encore.
    [Fact]
    public async Task Une_memoire_d_essai_abimee_est_mise_de_cote_avant_un_essai_neuf()
    {
        var vm = Vm();
        File.WriteAllText(Path.Combine(_dossier, "essai.json"), "{\"LevierId\":\"gpu.power_limit\",\"PoseALOrig");

        (await vm.DemarrerEssaiAsync(Id)).Should().BeTrue();

        Directory.GetFiles(_dossier, "essai.json.mis-de-cote-*").Should().ContainSingle()
            .Which.Should().Match(f => File.ReadAllText(f).Contains("PoseALOrig"));
    }

    private const string Reflex = "latency.reflex";

    /// <summary>Reflex posé par l'essai (0 → 1), puis passé à 2 par le joueur hors de DrDXT.</summary>
    private async Task<ScanViewModel> EssaiPuisChangeParLeJoueur()
    {
        var registre = Substitute.For<DrDxt.Application.Tweaks.Hardware.IRegistryReader>();
        registre.ReadDword(RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode")
            .Returns(Result<uint?>.Ok(1u));
        var vm = Vm(id: Reflex, origine: InversesDeTest.Pour(Reflex, "0"), posee: InversesDeTest.Pour(Reflex, "1"), registre: registre);
        (await vm.DemarrerEssaiAsync(Reflex)).Should().BeTrue();
        registre.ReadDword(RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode")
            .Returns(Result<uint?>.Ok(2u));
        return vm;
    }

    // La bascule « sans » rendrait la valeur d'avant par-dessus un réglage que le joueur a changé depuis. Rien n'est réécrit :
    // l'essai se met en pause, et c'est dit.
    [Fact]
    public async Task Une_bascule_n_ecrase_pas_un_reglage_change_depuis_par_le_joueur()
    {
        var vm = await EssaiPuisChangeParLeJoueur();

        await Jouer(vm, 90);

        _rendus.Should().Be(0);
        vm.MemoireEssai!.Lire()!.EnPause.Should().BeTrue();
        vm.LigneEssai.Should().Contain("Essai.ChangeDepuis");
    }

    // L'arrêt non plus : l'essai s'arrête, la valeur du joueur reste, et la ligne ne dit pas « l'état d'avant est revenu ».
    [Fact]
    public async Task L_arret_n_ecrase_pas_un_reglage_change_depuis_par_le_joueur()
    {
        var vm = await EssaiPuisChangeParLeJoueur();

        await vm.ArreterEssaiAsync();

        _rendus.Should().Be(0);
        vm.MemoireEssai!.Lire().Should().BeNull();
        vm.LigneEssai.Should().Contain("Essai.ArreteChangeDepuis");
    }

    // Le levier refuse la pose : l'essai ne part pas, et la ligne le dit au lieu de laisser le clic sans réponse.
    [Fact]
    public async Task Une_pose_refusee_au_lancement_de_l_essai_se_dit()
    {
        var vm = Vm();
        _levier.ApplyAsync().Returns(Task.FromResult(Result<TweakSnapshot>.Fail(ErrorCodes.HelperOpFailed, "refus")));

        (await vm.DemarrerEssaiAsync(Id)).Should().BeFalse();

        vm.LigneEssai.Should().StartWith("Essai.NonPose");
        vm.MemoireEssai!.Lire().Should().BeNull();
    }
}
