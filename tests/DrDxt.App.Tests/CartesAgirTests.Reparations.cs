using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>Réparations : écran de récupération, test mémoire, périphériques, droits des services, pilotes d'affichage, Windows Update.</summary>
public sealed partial class CartesAgirTests
{
    private static readonly RepairFinding RecuperationCoupee = new(
        "BCD", "bootstatuspolicy", "IgnoreAllFailures", null, RepairStatus.KnownHarmful,
        "un script a coupe l'ecran de recuperation.", RepairAction.Recommandation);

    // L'écran de récupération coupé est un point qu'Optimiser fait lui-même : il sort de ce qui reste à la main, le geste
    // part avec Optimiser, et le résultat le dit.
    [Fact]
    public async Task Optimiser_rend_l_ecran_de_recuperation_et_le_retire_de_ce_qui_reste_a_la_main()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(new[] { RecuperationCoupee }, helper, loc: k => k == "Apply.GesteFait" ? "{0} : {1}" : k);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.PointsALaMain.Should().NotContain(p => p.Entree.Cle == "Audit.BCD.bootstatuspolicy", "Optimiser va le faire");

        var lignes = await vm.GestesDuDocteurAsync();

        await helper.Received(1).ExecuteAsync(new DeleteBcdValueOp("bootstatuspolicy"), Arg.Any<CancellationToken>());
        lignes.Should().ContainSingle().Which.Should().Be("Bcd.Rendre.Titre : Bcd.Rendre.Fait");
        vm.BcdRendu.Should().BeTrue("Annuler reste sur la carte");
        (await vm.GestesDuDocteurAsync()).Should().BeEmpty("déjà rendu : rien à refaire");
    }

    [Fact]
    public async Task Rendre_puis_annuler_repose_exactement_la_valeur_lue()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(new[] { RecuperationCoupee }, helper);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.BcdARendre.Should().BeTrue("l'audit a lu l'ecran de recuperation coupe");

        await vm.RendreBcdCommand.ExecuteAsync(null);
        await helper.Received(1).ExecuteAsync(new DeleteBcdValueOp("bootstatuspolicy"), Arg.Any<CancellationToken>());
        vm.BcdRendu.Should().BeTrue();
        vm.BcdARendre.Should().BeFalse();
        // Le constat ne reste pas à côté de la carte « rendu » (défaut vu le 25/09, même que la remesure).
        vm.RepairFindings.Should().NotContain(v => v.DomaineCle == "BCD" && v.NomCle == "bootstatuspolicy");

        await vm.AnnulerBcdCommand.ExecuteAsync(null);
        await helper.Received(1).ExecuteAsync(
            new SetBcdValueOp("bootstatuspolicy", "IgnoreAllFailures"), Arg.Any<CancellationToken>());
        vm.BcdRendu.Should().BeFalse();
        vm.BcdARendre.Should().BeTrue("annule, la carte repropose le geste");
        vm.ErreurBcd.Should().BeNull();
        vm.RepairFindings.Should().ContainSingle(v => v.DomaineCle == "BCD" && v.NomCle == "bootstatuspolicy",
            "annulé, le réglage est revenu : le constat aussi");
    }

    // Le test mémoire jamais lancé : DrDXT le demande lui-même au prochain démarrage, et l'annule. Le redémarrage
    // reste au joueur.
    [Fact]
    public async Task Le_test_memoire_jamais_lance_porte_son_geste_qui_le_demande_et_l_annule()
    {
        var helper = Helper(Result.Ok());
        var jamais = new RepairFinding("STABILITE", "test memoire Windows", "jamais lancé", null, RepairStatus.Observed,
            "aucun résultat du test.", RepairAction.Recommandation, Remede: "1) mdsched.exe.");
        var vm = Vm(new[] { jamais }, helper);
        await vm.ScanCommand.ExecuteAsync(null);

        var geste = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes).Gestes!.Single();
        await geste.FaireCommand.ExecuteAsync(null);
        await helper.Received(1).ExecuteAsync(new ScheduleMemoryDiagnosticOp(), Arg.Any<CancellationToken>());
        geste.Fait.Should().BeTrue();

        await geste.AnnulerCommand.ExecuteAsync(null);
        await helper.Received(1).ExecuteAsync(new CancelMemoryDiagnosticOp(), Arg.Any<CancellationToken>());
    }

    // Le périphérique en échec se relance par l'identifiant lu ; rien à défaire, donc pas d'« Annuler ».
    [Fact]
    public async Task Un_peripherique_en_echec_se_relance_par_son_identifiant_lu()
    {
        var helper = Helper(Result.Ok());
        var enEchec = new RepairFinding("PERIPHERIQUES", "Contrôleur USB", "en échec (code 10)", "sans erreur", RepairStatus.Suspicious,
            "son pilote ne fonctionne pas.", RepairAction.Recommandation, Remede: "1) Gestionnaire de périphériques.",
            Cible: @"USB\VID_046D\5&1");
        var vm = Vm(new[] { enEchec }, helper);
        await vm.ScanCommand.ExecuteAsync(null);

        var geste = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes).Gestes!.Single();
        await geste.FaireCommand.ExecuteAsync(null);

        await helper.Received(1).ExecuteAsync(new RestartDeviceOp(@"USB\VID_046D\5&1"), Arg.Any<CancellationToken>());
        geste.PeutAnnuler.Should().BeFalse();
    }

    private static RepairFinding ServiceRemplacable(string? cible = "JeuServiceA") => new("SERVICES",
        "service Call of Duty Broker : son fichier peut être remplacé par un autre programme",
        @"C:\ProgramData\Activision\Call of Duty\JeuServiceA.exe", null, RepairStatus.Suspicious,
        "ce service tourne avec les droits du système (SYSTEM), mais son dossier appartient à un compte.",
        Remede: "1) Signale-le-lui.", Cible: cible);

    // Le service remplaçable porte « Corriger les droits » et son Annuler, par le nom du service seul. Gardé dans « Ce que
    // DrDXT a posé » pour un Annuler après une relance, jamais au manifeste : la désinstallation laisse la correction.
    [Fact]
    public async Task Un_service_remplacable_porte_corriger_les_droits_et_son_annuler()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"geste-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var recues = new List<PrivilegedOp>();
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var manifeste = Substitute.For<IDesiredStateStore>();
            var vm = Vm(new[] { ServiceRemplacable() }, HelperQuiNote(recues), journalLeviers: leviers, manifeste: manifeste);
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            await vm.ScanCommand.ExecuteAsync(null);

            var geste = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes).Gestes!.Single();
            geste.Titre.Should().Be("Geste.Droits.Titre");
            await geste.FaireCommand.ExecuteAsync(null);

            recues.Should().Equal(new CorrigerDroitsDuServiceOp("JeuServiceA"));
            var id = DrDxt.Application.Tweaks.LevierDeGeste.IdDroits("JeuServiceA");
            leviers.Lire().Should().ContainSingle(s => s.TweakId == id);
            vm.LeviersPoses.Should().ContainSingle(l => l.Id == id).Which.Titre.Should().Be("Leviers.Geste.Droits");
            await manifeste.DidNotReceive().CommitAsync(Arg.Any<DesiredState>());

            geste.PeutAnnuler.Should().BeTrue();
            await geste.AnnulerCommand.ExecuteAsync(null);
            recues.Last().Should().Be(new RendreDroitsDuServiceOp("JeuServiceA"));
            leviers.Lire().Should().BeEmpty();
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    [Fact]
    public async Task Sans_helper_corriger_les_droits_n_est_pas_propose()
    {
        var vm = Vm(new[] { ServiceRemplacable() }, helper: null);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Domaines.SelectMany(d => d.Lignes).Should().NotContain(l => l.AGestes);
    }

    // Un chemin non cité ne se corrige pas par les droits, et les tâches planifiées sont hors de ce geste.
    [Fact]
    public async Task Ni_un_chemin_non_cite_ni_une_tache_ne_portent_corriger_les_droits()
    {
        var nonCite = new RepairFinding("SERVICES", "chemin de service non cité : Svc", @"C:\Program.exe", null, RepairStatus.Suspicious,
            @"Windows essaie C:\Program.exe avant le vrai binaire.", Remede: "1) regedit.");
        var tache = new RepairFinding("TACHES", "tâche Maj : son fichier peut être remplacé par un autre programme", @"C:\Jeu\maj.exe", null,
            RepairStatus.Suspicious, "cette tâche tourne en SYSTEM.", Remede: "1) Signale-le.", Cible: "Maj");
        var vm = Vm(new[] { nonCite, tache }, Helper(Result.Ok()));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Domaines.SelectMany(d => d.Lignes).Should().NotContain(l => l.AGestes);
    }

    // Après une relance, la carte du geste n'existe plus (le service est protégé) : « Annuler » dans « Ce que DrDXT a posé »
    // rend les droits d'origine.
    [Fact]
    public async Task Les_droits_corriges_se_rendent_depuis_ce_que_DrDXT_a_pose_apres_une_relance()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"geste-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var recues = new List<PrivilegedOp>();
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var id = DrDxt.Application.Tweaks.LevierDeGeste.IdDroits("JeuServiceA");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(id, true, false, DrDxt.Application.Tweaks.LevierDeGeste.Snapshot(id,
                [new RendreDroitsDuServiceOp("JeuServiceA")], [new CorrigerDroitsDuServiceOp("JeuServiceA")]), null)], null));
            var relance = Vm(Array.Empty<RepairFinding>(), helper: HelperQuiNote(recues), journalLeviers: leviers);
            relance.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());

            await relance.AnnulerLevierAsync(id, parClic: true);

            recues.Should().Equal(new RendreDroitsDuServiceOp("JeuServiceA"));
            leviers.Lire().Should().BeEmpty();
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Un geste par paquet, chacun avec son « Annuler » qui le remet depuis l'export.
    [Fact]
    public async Task Chaque_vieux_pilote_d_affichage_se_retire_et_se_remet()
    {
        var helper = Helper(Result.Ok());
        var vieux = new RepairFinding("GPU DRIVER", "anciens pilotes d'affichage gardés", "NVIDIA 1 (oem98.inf), AMD 2 (oem41.inf)", null,
            RepairStatus.Suspicious, "2 paquets.", RepairAction.Recommandation, Remede: "1) Terminal.", Cible: "oem98.inf oem41.inf");
        var vm = Vm(new[] { vieux }, helper, loc: k => k + ":{0}");
        await vm.ScanCommand.ExecuteAsync(null);

        var gestes = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes).Gestes!;
        gestes.Select(g => g.Titre).Should().Equal("Geste.Pilote.Titre:oem98.inf", "Geste.Pilote.Titre:oem41.inf");
        await gestes[1].FaireCommand.ExecuteAsync(null);
        await gestes[1].AnnulerCommand.ExecuteAsync(null);

        await helper.Received(1).ExecuteAsync(new RemoveDriverPackageOp("oem41.inf"), Arg.Any<CancellationToken>());
        await helper.Received(1).ExecuteAsync(new RestoreDriverPackageOp("oem41.inf"), Arg.Any<CancellationToken>());
        await helper.DidNotReceive().ExecuteAsync(new RemoveDriverPackageOp("oem98.inf"), Arg.Any<CancellationToken>());
    }

    // Un geste fait garde son « Annuler » quand la carte se refait, même si le scan suivant ne propose plus ce paquet.
    [Fact]
    public async Task Un_pilote_retire_garde_son_annuler_apres_un_nouveau_scan()
    {
        var helper = Helper(Result.Ok());
        RepairFinding Constat(string cible) => new("GPU DRIVER", "anciens pilotes d'affichage gardés", "x", null,
            RepairStatus.Suspicious, "paquets.", RepairAction.Recommandation, Remede: "1) Terminal.", Cible: cible);
        var audit = new List<RepairFinding> { Constat("oem98.inf oem41.inf") };
        var vm = Vm(audit, helper, loc: k => k + ":{0}");
        await vm.ScanCommand.ExecuteAsync(null);
        var retire = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes).Gestes![1];
        await retire.FaireCommand.ExecuteAsync(null);

        audit[0] = Constat("oem98.inf");
        await vm.ScanCommand.ExecuteAsync(null);

        var gestes = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes).Gestes!;
        gestes.Should().Contain(retire);
        retire.PeutAnnuler.Should().BeTrue();
    }

    // La recherche de Windows Update ne demande pas de droits : elle se propose même sans helper, et ne se défait pas.
    [Fact]
    public async Task Une_verification_ancienne_de_Windows_Update_se_relance_sans_droits()
    {
        var ancienne = new RepairFinding("WINDOWS UPDATE", "en attente", "aucune", null, RepairStatus.Suspicious,
            "rien en attente, mais pas vérifié depuis 30 jours.", RepairAction.Recommandation, Remede: "1) Paramètres.",
            Cible: DrDxt.Detection.Hardware.MisesAJourEnAttente.CibleRecherche);
        var vm = Vm(new[] { ancienne }, helper: null);
        var recherches = 0;
        vm.ChercherWindowsUpdate = () => { recherches++; return Task.FromResult(Result.Ok()); };
        await vm.ScanCommand.ExecuteAsync(null);

        var geste = vm.Domaines.SelectMany(d => d.Lignes).Single(l => l.AGestes).Gestes!.Single();
        await geste.FaireCommand.ExecuteAsync(null);

        recherches.Should().Be(1);
        geste.Fait.Should().BeTrue();
        geste.PeutAnnuler.Should().BeFalse();
    }

    [Fact]
    public async Task Sans_helper_aucun_geste_n_est_propose()
    {
        var jamais = new RepairFinding("STABILITE", "test memoire Windows", "jamais lancé", null, RepairStatus.Observed,
            "aucun résultat du test.", RepairAction.Recommandation, Remede: "1) mdsched.exe.");
        var vm = Vm(new[] { jamais }, helper: null);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Domaines.SelectMany(d => d.Lignes).Should().NotContain(l => l.AGestes);
    }

    [Fact]
    public async Task Un_refus_du_helper_se_dit_et_la_carte_ne_pretend_pas_l_avoir_fait()
    {
        var helper = Helper(Result.Fail(ErrorCodes.TweakNotApplicable, "UAC refuse"));
        var vm = Vm(new[] { RecuperationCoupee }, helper);
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.RendreBcdCommand.ExecuteAsync(null);

        vm.BcdRendu.Should().BeFalse();
        vm.BcdARendre.Should().BeTrue();
        vm.ErreurBcd.Should().Contain("Erreur.Generique").And.NotContain("UAC refuse");
        vm.RepairFindings.Should().Contain(v => v.DomaineCle == "BCD", "rien n'a été fait : le constat reste");
    }

    [Fact]
    public async Task Le_retrait_reste_annulable_apres_fermeture_de_DrDXT_ou_redemarrage()
    {
        var fichier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"drdxt-bcd-{Guid.NewGuid():N}.json");
        try
        {
            var helper = Helper(Result.Ok());
            var avant = Vm(new[] { RecuperationCoupee }, helper);
            avant.GardeBcd = new DrDxt.Application.Safety.GardeDuBcdRetire(fichier);
            await avant.ScanCommand.ExecuteAsync(null);
            await avant.RendreBcdCommand.ExecuteAsync(null);

            // Nouvelle session : l'audit ne voit plus le reglage, puisqu'il a ete retire.
            var apres = Vm(Array.Empty<RepairFinding>(), helper);
            apres.GardeBcd = new DrDxt.Application.Safety.GardeDuBcdRetire(fichier);
            await apres.ScanCommand.ExecuteAsync(null);
            apres.BcdRendu.Should().BeTrue("la carte promet « Réversible » : l'Annuler survit a la session");

            await apres.AnnulerBcdCommand.ExecuteAsync(null);
            await helper.Received(1).ExecuteAsync(
                new SetBcdValueOp("bootstatuspolicy", "IgnoreAllFailures"), Arg.Any<CancellationToken>());
            System.IO.File.Exists(fichier).Should().BeFalse("annule, il n'y a plus rien a garder");
        }
        finally { System.IO.File.Delete(fichier); }
    }

    // La garde vit sous %LOCALAPPDATA%, ecrivable par tout programme du compte. Modifiee, elle
    // faisait poser par « Annuler » un autre element BCD permis (useplatformclock Yes force le HPET), alors
    // que la carte ne retire que bootstatuspolicy.
    [Fact]
    public async Task Une_garde_modifiee_pour_un_autre_element_ne_propose_aucun_annuler()
    {
        var fichier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"drdxt-bcd-{Guid.NewGuid():N}.json");
        try
        {
            System.IO.File.WriteAllText(fichier, """{"Element":"useplatformclock","Valeur":"Yes"}""");
            var helper = Helper(Result.Ok());
            var vm = Vm(Array.Empty<RepairFinding>(), helper);
            vm.GardeBcd = new DrDxt.Application.Safety.GardeDuBcdRetire(fichier);
            await vm.ScanCommand.ExecuteAsync(null);

            vm.BcdRendu.Should().BeFalse();
            await vm.AnnulerBcdCommand.ExecuteAsync(null);
            await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
        }
        finally { System.IO.File.Delete(fichier); }
    }

    // bcdedit écrit tout de suite : sans garde sur le disque, le retrait n'aurait plus d'annulation après une coupure.
    [Fact]
    public async Task Sans_garde_ecrite_le_reglage_n_est_pas_retire()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"drdxt-bcd-{Guid.NewGuid():N}");
        System.IO.File.WriteAllText(dossier, "un fichier, pas un dossier");
        try
        {
            var helper = Helper(Result.Ok());
            var vm = Vm(new[] { RecuperationCoupee }, helper);
            vm.GardeBcd = new DrDxt.Application.Safety.GardeDuBcdRetire(System.IO.Path.Combine(dossier, "bcd-rendu.json"));
            await vm.ScanCommand.ExecuteAsync(null);

            await vm.RendreBcdCommand.ExecuteAsync(null);

            await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
            vm.ErreurBcd.Should().Be("Bcd.Rendre.GardeImpossible");
            vm.BcdARendre.Should().BeTrue("rien n'a été retiré");
        }
        finally { System.IO.File.Delete(dossier); }
    }

    [Fact]
    public async Task Un_refus_du_helper_efface_la_garde_ecrite_avant_lui()
    {
        var fichier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"drdxt-bcd-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Vm(new[] { RecuperationCoupee }, Helper(Result.Fail(ErrorCodes.TweakNotApplicable, "UAC refuse")));
            vm.GardeBcd = new DrDxt.Application.Safety.GardeDuBcdRetire(fichier);
            await vm.ScanCommand.ExecuteAsync(null);

            await vm.RendreBcdCommand.ExecuteAsync(null);

            System.IO.File.Exists(fichier).Should().BeFalse("le réglage est toujours là : une garde ferait proposer un faux Annuler");
        }
        finally { System.IO.File.Delete(fichier); }
    }

    // Le helper s'est tu après avoir reçu l'ordre : bcdedit a pu retirer la valeur. Effacée, la garde laissait un retrait sans
    // annulation ; gardée, la session suivante propose l'Annuler si l'audit ne voit plus la valeur.
    [Theory]
    [InlineData(ErrorCodes.HelperNoReply)]
    [InlineData(ErrorCodes.HelperNoReplyJournalisee)]
    public async Task Une_reponse_manquante_du_helper_garde_l_annuler(string code)
    {
        var fichier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"drdxt-bcd-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Vm(new[] { RecuperationCoupee }, Helper(Result.Fail(code, "pipe broken")));
            vm.GardeBcd = new DrDxt.Application.Safety.GardeDuBcdRetire(fichier);
            await vm.ScanCommand.ExecuteAsync(null);

            await vm.RendreBcdCommand.ExecuteAsync(null);

            System.IO.File.Exists(fichier).Should().BeTrue("le retrait a peut-être eu lieu : son Annuler reste possible");
            vm.ErreurBcd.Should().NotBeNullOrEmpty();

            // Session suivante : la valeur n'est plus lue, l'Annuler est proposé.
            var helper = Helper(Result.Ok());
            var apres = Vm(Array.Empty<RepairFinding>(), helper);
            apres.GardeBcd = new DrDxt.Application.Safety.GardeDuBcdRetire(fichier);
            await apres.ScanCommand.ExecuteAsync(null);
            apres.BcdRendu.Should().BeTrue();
        }
        finally { System.IO.File.Delete(fichier); }
    }

    [Fact]
    public async Task Sans_ecran_de_recuperation_coupe_la_carte_n_existe_pas()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.BcdVisible.Should().BeFalse();
        await vm.RendreBcdCommand.ExecuteAsync(null);
        await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Le_test_memoire_lance_depuis_la_carte_rend_son_resultat_et_libere_la_carte()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        vm.DureeDuTestMemoire = TimeSpan.FromMilliseconds(50);
        vm.VolumeDuTestMemoire = 1L << 20;

        await vm.LancerTestMemoireCommand.ExecuteAsync(null);

        vm.TestMemoireEnCours.Should().BeFalse();
        vm.TestMemoireATermine.Should().BeTrue();
        vm.TestMemoireEnDefaut.Should().BeFalse();
        vm.ResultatTestMemoire.Should().StartWith("aucune erreur").And.Contain("ne prouve pas");
        vm.ProgressionTestMemoire.Should().Be(1);
    }

    [Fact]
    public async Task Arreter_coupe_un_test_long_entre_deux_passes()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        vm.DureeDuTestMemoire = TimeSpan.FromMinutes(10);
        vm.VolumeDuTestMemoire = 1L << 20;

        var test = vm.LancerTestMemoireCommand.ExecuteAsync(null);
        await Task.Delay(100);
        vm.TestMemoireEnCours.Should().BeTrue("témoin : le test tournait");
        vm.ArreterTestMemoireCommand.Execute(null);

        (await Task.WhenAny(test, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(test,
            "l'arrêt doit rendre la main bien avant les dix minutes");
        vm.TestMemoireEnCours.Should().BeFalse();
        vm.ResultatTestMemoire.Should().NotBeNull("un test arrêté dit ce qu'il a fini");
    }

    private static IElevatedClient HelperQuiNote(List<PrivilegedOp> recues)
    {
        var h = Substitute.For<IElevatedClient>();
        h.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(c => { recues.Add(c.Arg<PrivilegedOp>()); return Task.FromResult(Result.Ok()); });
        return h;
    }

    // Un paquet de pilote retiré par le geste du docteur est gardé comme un levier : il est dans « Ce que DrDXT a posé », et
    // « Annuler » le remet même après une relance, quand la carte du geste n'existe plus.
    [Fact]
    public async Task Un_paquet_de_pilote_retire_se_remet_depuis_ce_que_DrDXT_a_pose()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"geste-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var recues = new List<PrivilegedOp>();
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: HelperQuiNote(recues), journalLeviers: leviers);
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            vm.ExportDuPiloteExiste = _ => true;

            await vm.GestePilote("oem12.inf").FaireCommand.ExecuteAsync(null);
            var id = DrDxt.Application.Tweaks.LevierDeGeste.IdPilote("oem12.inf");
            leviers.Lire().Should().ContainSingle(s => s.TweakId == id);
            vm.LeviersPoses.Should().ContainSingle(l => l.Id == id).Which.Titre.Should().Be("Leviers.Geste.Pilote");

            var relance = Vm(Array.Empty<RepairFinding>(), helper: HelperQuiNote(recues), journalLeviers: leviers);
            relance.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            relance.ExportDuPiloteExiste = _ => true;
            await relance.AnnulerLevierAsync(id, parClic: true);

            recues.Should().Equal(new RemoveDriverPackageOp("oem12.inf"), new RestoreDriverPackageOp("oem12.inf"));
            leviers.Lire().Should().BeEmpty();
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // L'export du paquet n'est plus là : rien n'est envoyé, le paquet sort de la liste, et c'est dit.
    [Fact]
    public async Task Sans_export_le_paquet_sort_de_la_liste_et_c_est_dit()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"geste-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var recues = new List<PrivilegedOp>();
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var id = DrDxt.Application.Tweaks.LevierDeGeste.IdPilote("oem12.inf");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(id, true, false, DrDxt.Application.Tweaks.LevierDeGeste.Snapshot(id,
                [new RestoreDriverPackageOp("oem12.inf")], [new RemoveDriverPackageOp("oem12.inf")]), null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: HelperQuiNote(recues), journalLeviers: leviers);
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            vm.ExportDuPiloteExiste = _ => false;

            await vm.AnnulerLevierAsync(id, parClic: true);

            recues.Should().BeEmpty();
            leviers.Lire().Should().BeEmpty();
            vm.MessageLeviers.Should().Be("Geste.Pilote.ExportAbsent");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Un geste gardé est un choix du joueur, posé sur son clic : ni la garde d'incident ni le retrait automatique ne le
    // rendent, même daté juste avant un arrêt brutal.
    [Fact]
    public async Task Un_geste_garde_reste_hors_de_la_garde_d_incident()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"geste-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var recues = new List<PrivilegedOp>();
            var maintenant = DateTimeOffset.Now;
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var id = DrDxt.Application.Tweaks.LevierDeGeste.IdPilote("oem12.inf");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(id, true, false, DrDxt.Application.Tweaks.LevierDeGeste.Snapshot(id,
                [new RestoreDriverPackageOp("oem12.inf")], [new RemoveDriverPackageOp("oem12.inf")]), null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: HelperQuiNote(recues), journalLeviers: leviers,
                lireStabilite: () => (null, [new ArretClasse(maintenant.AddHours(-12), FamilleDArret.SansCode)]));
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            vm.NoterPoses([id], maintenant.AddDays(-1));
            vm.LevierParId = _ => null;
            vm.ExportDuPiloteExiste = _ => true;

            await vm.RetirerCeQuiCouteAsync([]);

            recues.Should().BeEmpty();
            leviers.Lire().Should().ContainSingle();
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }
}
