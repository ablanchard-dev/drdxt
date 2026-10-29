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
public sealed partial class EssaiEnAlternanceTests : IDisposable
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
}
