using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// 24/09/2026 — les cartes « Agir » (spec §3c du registre de boucle). Leurs etats « rendu » et
/// « allume » ne se photographient pas sans ecrire sur la machine : ils se prouvent ici, avec un
/// helper factice qui enregistre ce qu'on lui demande.
/// </summary>
public sealed class CartesAgirTests
{
    private static readonly RepairFinding RecuperationCoupee = new(
        "BCD", "bootstatuspolicy", "IgnoreAllFailures", null, RepairStatus.KnownHarmful,
        "un script a coupe l'ecran de recuperation.", RepairAction.Recommandation);

    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "AMD", 16L << 30, "1", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static ScanViewModel Vm(IReadOnlyList<RepairFinding> audit, IElevatedClient? helper,
        Func<string, string, string>? writeReport = null, DrDxt.Measurement.Cpu.CpuBaselineService? cpu = null,
        Func<string, string>? loc = null, int? jeu = null, DrDxt.Measurement.PerformanceJournalStore? journal = null,
        DrDxt.Application.Tweaks.JournalDesLeviersAppliques? journalLeviers = null,
        DrDxt.Application.Tweaks.Hardware.IRegistryReader? registreGains = null)
    {
        var locator = Substitute.For<DrDxt.App.Platform.IGameLocator>();
        locator.LocateRunningGamePid().Returns(jeu);
        locator.JeuOuvert().Returns(jeu is not null);
        var st = new SettingsState(TriState.On, TriState.On,
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On,
            Array.Empty<int>(), TriState.Unknown);
        var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(st));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(bl));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), st, new List<Finding>(),
              68, bl, new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, loc ?? (k => k), repairAudit: () => audit, helper: helper, writeReport: writeReport,
            machineId: cpu is null ? null : () => "m1", cpuBaseline: cpu, gameLocator: locator, performanceJournal: journal,
            journalLeviers: journalLeviers, registreGains: registreGains);
    }

    private static IElevatedClient Helper(Result reponse)
    {
        var h = Substitute.For<IElevatedClient>();
        h.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(reponse));
        return h;
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

    [Fact]
    public async Task Un_refus_du_helper_se_dit_et_la_carte_ne_pretend_pas_l_avoir_fait()
    {
        var helper = Helper(Result.Fail(ErrorCodes.TweakNotApplicable, "UAC refuse"));
        var vm = Vm(new[] { RecuperationCoupee }, helper);
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.RendreBcdCommand.ExecuteAsync(null);

        vm.BcdRendu.Should().BeFalse();
        vm.BcdARendre.Should().BeTrue();
        vm.ErreurBcd.Should().Contain("UAC refuse");
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
            avant.FichierBcdRendu = fichier;
            await avant.ScanCommand.ExecuteAsync(null);
            await avant.RendreBcdCommand.ExecuteAsync(null);

            // Nouvelle session : l'audit ne voit plus le reglage, puisqu'il a ete retire.
            var apres = Vm(Array.Empty<RepairFinding>(), helper);
            apres.FichierBcdRendu = fichier;
            await apres.ScanCommand.ExecuteAsync(null);
            apres.BcdRendu.Should().BeTrue("la carte promet « Réversible » : l'Annuler survit a la session");

            await apres.AnnulerBcdCommand.ExecuteAsync(null);
            await helper.Received(1).ExecuteAsync(
                new SetBcdValueOp("bootstatuspolicy", "IgnoreAllFailures"), Arg.Any<CancellationToken>());
            System.IO.File.Exists(fichier).Should().BeFalse("annule, il n'y a plus rien a garder");
        }
        finally { System.IO.File.Delete(fichier); }
    }

    // 26/09/2026 : la garde vit sous %LOCALAPPDATA%, ecrivable par tout programme du compte. Modifiee, elle
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
            vm.FichierBcdRendu = fichier;
            await vm.ScanCommand.ExecuteAsync(null);

            vm.BcdRendu.Should().BeFalse();
            await vm.AnnulerBcdCommand.ExecuteAsync(null);
            await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
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
    public async Task Sans_mesure_en_jeu_la_bascule_ping_refuse_en_le_disant_et_n_ecrit_rien()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.PlafondEnvoiIndisponible.Should().BeTrue("aucun bufferbloat mesure hors jeu");

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeFalse();
        vm.ErreurPlafondEnvoi.Should().Contain("Ping.Protection.Echec");
        await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Le_rapport_exporte_ne_porte_pas_le_nom_du_joueur()
    {
        // Un constat dont la valeur contient un chemin du profil : le rapport est fait pour être envoyé.
        var chemin = System.IO.Path.Combine("C:" + System.IO.Path.DirectorySeparatorChar, "Users", "alicejoueuse", "AppData", "jeu.cfg");
        var constat = new RepairFinding("STABILITE", "fichier du jeu", chemin, null, RepairStatus.Suspicious, "lu.");
        string? ecrit = null;
        var vm = Vm(new[] { constat }, helper: null, writeReport: (_, texte) => { ecrit = texte; return "rapport.txt"; });
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.ExportReportCommand.ExecuteAsync(null);

        ecrit.Should().NotBeNull();
        ecrit.Should().Contain("Fichier du jeu", "témoin : le constat est bien dans le rapport");
        ecrit.Should().NotContain("alicejoueuse");
        ecrit.Should().Contain("jeu.cfg", "la fin du chemin reste lisible, seul le nom est masqué");
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

    // Un exe qu'aucune machine ne fait tourner : le mode s'allume sans jamais poser le plafond.
    private const string JeuAbsent = "drdxt-jeu-absent-des-tests.exe";

    // 27/09/2026 : la veille du ping. Sans scan en partie saturee, la carte n'offrait rien a faire.
    private static IReadOnlyList<DrDxt.Measurement.Network.EchantillonEnvoi> Fenetre(double pingEnCharge)
        => Enumerable.Range(0, 25).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(1, 10))
            .Concat(Enumerable.Range(0, 25).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(100, pingEnCharge))).ToList();

    [Fact]
    public void Sans_mesure_la_carte_propose_de_surveiller_les_parties()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
        vm.CollecteurFenetre = (_, _) => Task.FromResult(Fenetre(40));
        vm.VeillePingProposee.Should().BeTrue();
        vm.LabelVeilleBascule.Should().Be("Ping.Veille.Libelle");

        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
        vm.VeillePingProposee.Should().BeFalse("une mesure existe : la bascule de la protection prend la place");
    }

    [Fact]
    public async Task La_veille_allume_la_protection_sur_un_bufferbloat_vu_en_partie()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.CollecteurFenetre = (_, _) => Task.FromResult(Fenetre(40));
        vm.JeuPourLaVeille = () => JeuAbsent;
        vm.VeillePingActive = true;

        await vm.SurveillerLesPartiesAsync(CancellationToken.None);

        vm.VeillePingActive.Should().BeFalse();
        vm.PlafondEnvoiDisponible.Should().BeTrue("le plafond vient du verdict de la partie");
        vm.PlafondEnvoiActif.Should().BeTrue();
        vm.ErreurPlafondEnvoi.Should().BeNull();
        await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>());
        await vm.DesactiverPlafondEnvoiAsync();
    }

    // Revue du 08/10 : pendant la veille, la commande restait grisée (l'interrupteur ne s'éteignait plus) ; et un clic sans
    // effet (veille plus proposée) laissait l'interrupteur coché par WPF sans que l'état le redise.
    [Fact]
    public async Task L_interrupteur_de_la_veille_reste_cliquable_et_redit_son_etat()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
        vm.CollecteurFenetre = (_, _) => Task.FromResult(Fenetre(12));
        vm.JeuPourLaVeille = () => null;
        vm.AttenteVeille = (d, ct) => Task.Delay(Timeout.Infinite, ct);

        var enCours = vm.BasculerVeillePingCommand.ExecuteAsync(null);
        vm.VeillePingActive.Should().BeTrue();
        vm.BasculerVeillePingCommand.CanExecute(null).Should().BeTrue("on doit pouvoir l'éteindre pendant qu'elle tourne");
        await vm.BasculerVeillePingCommand.ExecuteAsync(null);
        await enCours;
        vm.VeillePingActive.Should().BeFalse();

        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
        var redit = new List<string?>();
        vm.PropertyChanged += (_, e) => redit.Add(e.PropertyName);
        await vm.BasculerVeillePingCommand.ExecuteAsync(null);
        vm.VeillePingActive.Should().BeFalse();
        redit.Should().Contain(nameof(vm.VeillePingActive), "l'interrupteur coché par le clic doit se recaler");
    }

    [Fact]
    public async Task La_veille_allumee_se_relance_au_lancement_suivant_et_s_oublie_une_fois_arretee()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-veille-vm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var chemin = System.IO.Path.Combine(dossier, "v.json");
            DrDxt.App.ViewModels.ScanViewModel Monter()
            {
                var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
                vm.CollecteurFenetre = (_, _) => Task.FromResult(Fenetre(12));
                vm.JeuPourLaVeille = () => null; // aucune partie : la veille attend
                vm.AttenteVeille = (d, ct) => Task.Delay(Timeout.Infinite, ct);
                vm.MemoireVeille = new DrDxt.Application.Network.VeillePingMemorisee(chemin);
                return vm;
            }

            var premier = Monter();
            _ = premier.BasculerVeillePingCommand.ExecuteAsync(null);
            premier.VeillePingActive.Should().BeTrue();

            var second = Monter();
            second.RallumerVeillePing();
            second.VeillePingActive.Should().BeTrue("le joueur l'avait laissee allumee");

            await second.BasculerVeillePingCommand.ExecuteAsync(null);
            second.VeillePingActive.Should().BeFalse();
            var troisieme = Monter();
            troisieme.RallumerVeillePing();
            troisieme.VeillePingActive.Should().BeFalse("arretee, elle ne revient pas");

            await premier.BasculerVeillePingCommand.ExecuteAsync(null);
        }
        finally
        {
            if (System.IO.Directory.Exists(dossier)) System.IO.Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public async Task Un_plafond_trouve_eteint_la_veille_memorisee()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-veille-vm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memoire = new DrDxt.Application.Network.VeillePingMemorisee(System.IO.Path.Combine(dossier, "v.json"));
            memoire.Allumer();
            var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
            vm.CollecteurFenetre = (_, _) => Task.FromResult(Fenetre(40));
            vm.JeuPourLaVeille = () => JeuAbsent;
            vm.MemoireVeille = memoire;

            await vm.SurveillerLesPartiesAsync(CancellationToken.None);

            memoire.EstAllumee().Should().BeFalse("la memoire du plafond prend le relais");
            await vm.DesactiverPlafondEnvoiAsync();
        }
        finally
        {
            if (System.IO.Directory.Exists(dossier)) System.IO.Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public async Task Un_envoi_sain_ne_pose_rien_et_l_arret_ne_laisse_rien()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        using var arret = new CancellationTokenSource();
        var fenetres = 0;
        vm.CollecteurFenetre = (_, _) =>
        {
            if (++fenetres == 3) arret.Cancel();
            return Task.FromResult(Fenetre(12));
        };
        vm.JeuPourLaVeille = () => JeuAbsent;

        await vm.SurveillerLesPartiesAsync(arret.Token);

        vm.PlafondEnvoiDisponible.Should().BeFalse();
        vm.PlafondEnvoiActif.Should().BeFalse();
        await helper.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task Apres_une_mesure_en_jeu_la_bascule_ouvre_le_bail_puis_retire_tout_en_s_eteignant()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
        vm.PlafondEnvoiDisponible.Should().BeTrue();
        // Le plafond est dit avant le clic (clé de ressource : le localiseur de test rend la clé).
        vm.TextePlafondEnvoi.Should().Contain("Ping.Protection.Plafond");

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        vm.PlafondEnvoiActif.Should().BeTrue();
        vm.LabelPingEtat.Should().Be("Ping.Protection.Active");
        vm.ErreurPlafondEnvoi.Should().BeNull();
        await helper.Received(1).ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>());

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        vm.PlafondEnvoiActif.Should().BeFalse();
        vm.LabelPingEtat.Should().Be("Ping.Protection.Inactive");
        await helper.Received().ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is RemoveUploadCapOp), Arg.Any<CancellationToken>());
    }

    // 28/09/2026 : la protection retenue part aussi au service de démarrage, par le helper (seul à écrire sous
    // %ProgramData%\DrDxt). Éteinte, elle y est oubliée : le service ne plafonne plus rien.
    [Fact]
    public async Task La_bascule_retient_la_protection_pour_le_service_puis_l_oublie()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        await helper.Received(1).ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is RetainServiceUploadCapOp
                                      && ((RetainServiceUploadCapOp)o).GameExeName == JeuAbsent
                                      && ((RetainServiceUploadCapOp)o).CapBitsPerSecond == 20_000_000),
            Arg.Any<CancellationToken>());

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        await helper.Received(1).ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is ForgetServiceUploadCapOp), Arg.Any<CancellationToken>());
    }

    // 28/09/2026 : un seul poseur. Service de démarrage en marche : il porte la protection, l'App n'arme pas son
    // propre mode (deux poseurs se retireraient l'un l'autre le plafond quand l'App se ferme en pleine partie).
    [Fact]
    public async Task Service_en_marche_l_App_n_arme_pas_son_propre_mode_mais_la_protection_est_active()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.ServiceDemarrageActif = () => true;
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeTrue();
        await helper.DidNotReceive().ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>());
        await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is RetainServiceUploadCapOp), Arg.Any<CancellationToken>());

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeFalse();
        await helper.Received(1).ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is ForgetServiceUploadCapOp), Arg.Any<CancellationToken>());
    }

    // 28/09/2026 : service en marche, l'App ne pose rien elle-même ; si le helper refuse de retenir la protection
    // pour le service (UAC déclinée), personne ne la porte. La carte disait pourtant « active ».
    [Fact]
    public async Task Service_en_marche_un_refus_de_retenir_laisse_la_protection_eteinte_et_le_dit()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Fail(ErrorCodes.TweakNotApplicable, "UAC refuse")));
        vm.ServiceDemarrageActif = () => true;
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeFalse("personne ne porte la protection");
        vm.ErreurPlafondEnvoi.Should().NotBeNull();
    }

    // 28/09/2026 : symétrique du refus à l'allumage. Éteindre demande au helper d'oublier la protection sous
    // %ProgramData% ; refusé, le service continuait de plafonner à chaque partie alors que la carte disait « inactive ».
    [Fact]
    public async Task Service_en_marche_un_refus_d_oublier_laisse_la_protection_active_et_le_dit()
    {
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.ServiceDemarrageActif = () => true;
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        vm.PlafondEnvoiActif.Should().BeTrue("témoin : allumée et retenue pour le service");

        helper.ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is ForgetServiceUploadCapOp), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.TweakNotApplicable, "UAC refuse")));
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeTrue("le service la porte toujours");
        vm.ErreurPlafondEnvoi.Should().NotBeNull();
    }

    // 28/09/2026 : la carte disait « elle n'agit que pendant que DrDXT est ouvert ». Faux dès que le service de
    // démarrage la porte : le texte suit celui qui pose le plafond.
    [Fact]
    public void La_carte_dit_qui_porte_la_protection()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));

        vm.LabelPingDescription.Should().Be("Ping.Protection.Description");

        vm.ServiceDemarrageActif = () => true;
        vm.LabelPingDescription.Should().Be("Ping.Protection.Description.Service");
    }

    // 28/09/2026, vu sur photo : la fenêtre s'affiche avant que l'App branche la question du service, et la carte
    // gardait le texte lu à l'affichage. Brancher la question doit prévenir la carte.
    [Fact]
    public void Brancher_la_question_du_service_previent_la_carte()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
        var prevenus = new List<string?>();
        vm.PropertyChanged += (_, e) => prevenus.Add(e.PropertyName);

        vm.ServiceDemarrageActif = () => true;

        prevenus.Should().Contain(nameof(vm.LabelPingDescription));
    }

    [Fact]
    public void La_carte_nomme_le_jeu_que_la_protection_surveille()
    {
        // 25/09/2026 : la carte disait « le jeu, lui, n'est pas plafonne » sans le nommer. Nomme, on sait
        // quel jeu est protege ; et un fichier retenu modifie (explorer.exe) se voit tout de suite.
        var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()),
            loc: k => k == "Ping.Protection.Plafond" ? "{0:0.#} Mbit/s, jeu : {1}" : k);
        vm.RetenirMesureEnvoi(20_000_000, "cod.exe");
        vm.TextePlafondEnvoi.Should().Contain("cod.exe");
    }

    [Fact]
    public async Task Une_seconde_activation_pendant_l_attente_de_la_premiere_n_ouvre_pas_un_second_mode()
    {
        // 25/09/2026 : au lancement, le rallumage attend le helper (UAC) ; un clic pendant ce temps
        // passait le garde (le mode n'est retenu qu'apres l'attente). Deux modes, deux boucles : en
        // eteignant, seule la seconde s'arretait, et la premiere reposait le plafond au jeu suivant.
        var bail = new TaskCompletionSource<Result>();
        var helper = Substitute.For<IElevatedClient>();
        helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());
        helper.ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>()).Returns(bail.Task);
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        var premiere = vm.ActiverPlafondEnvoiAsync();
        var seconde = vm.ActiverPlafondEnvoiAsync();
        bail.SetResult(Result.Ok());
        await Task.WhenAll(premiere, seconde);

        await helper.Received(1).ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is EstablishLeaseOp), Arg.Any<CancellationToken>());
        await vm.DesactiverPlafondEnvoiAsync();
    }

    // 28/09/2026 : une protection allumée avant le service n'était retenue que sous %LOCALAPPDATA%. Au premier
    // lancement avec le service, la carte disait « le service la porte » alors qu'il n'avait rien à porter. Elle lui
    // est transmise une fois (une invite, comme le rallumage d'avant) ; déjà retenue, le lancement ne demande rien.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Service_en_marche_le_rallumage_transmet_au_service_ce_qu_il_ne_porte_pas_encore(bool dejaRetenu, int ordres)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-plafond-vm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memoire = new DrDxt.Application.Network.PlafondEnvoiMemorise(System.IO.Path.Combine(dossier, "p.json"));
            memoire.Retenir(new(JeuAbsent, 20_000_000, DateTimeOffset.Now));
            var helper = Helper(Result.Ok());
            var vm = Vm(Array.Empty<RepairFinding>(), helper);
            vm.MemoirePlafond = memoire;
            vm.ServiceDemarrageActif = () => true;
            vm.ServicePorteUnRetenu = () => dejaRetenu;

            await vm.RallumerPlafondEnvoiAsync();

            vm.PlafondEnvoiActif.Should().BeTrue();
            helper.ReceivedCalls().Count(c => c.GetArguments()[0] is RetainServiceUploadCapOp).Should().Be(ordres);
            await vm.DesactiverPlafondEnvoiAsync();
        }
        finally
        {
            if (System.IO.Directory.Exists(dossier)) System.IO.Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public async Task La_protection_allumee_se_rallume_au_lancement_suivant_et_s_oublie_une_fois_eteinte()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-plafond-vm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memoire = new DrDxt.Application.Network.PlafondEnvoiMemorise(System.IO.Path.Combine(dossier, "p.json"));
            var vm = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
            vm.MemoirePlafond = memoire;
            vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);
            await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
            memoire.Lire()!.Exe.Should().Be(JeuAbsent);

            // Un nouveau lancement : un autre ViewModel, aucun scan, le meme fichier.
            var relance = Vm(Array.Empty<RepairFinding>(), Helper(Result.Ok()));
            relance.MemoirePlafond = memoire;
            await relance.RallumerPlafondEnvoiAsync();
            relance.PlafondEnvoiActif.Should().BeTrue("le joueur l'avait laissee allumee");

            await relance.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
            memoire.Lire().Should().BeNull("eteinte, elle ne revient plus");
            await vm.DesactiverPlafondEnvoiAsync();
        }
        finally
        {
            if (System.IO.Directory.Exists(dossier)) System.IO.Directory.Delete(dossier, recursive: true);
        }
    }

    // Le rallumage part au lancement sans être attendu : une exception y disparaîtrait sans un mot,
    // et le joueur croirait sa protection revenue.
    [Fact]
    public async Task Un_rallumage_qui_leve_le_dit_au_lieu_de_se_taire()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-plafond-leve-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memoire = new DrDxt.Application.Network.PlafondEnvoiMemorise(System.IO.Path.Combine(dossier, "p.json"));
            memoire.Retenir(new(JeuAbsent, 20_000_000, DateTimeOffset.Now));
            var helper = Substitute.For<IElevatedClient>();
            helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
                .Returns<Task<Result>>(_ => throw new InvalidOperationException("panne imprévue"));
            var vm = Vm(Array.Empty<RepairFinding>(), helper);
            vm.MemoirePlafond = memoire;

            await vm.Invoking(v => v.RallumerPlafondEnvoiAsync()).Should().NotThrowAsync();
            vm.ErreurPlafondEnvoi.Should().NotBeNullOrEmpty();
            vm.PlafondEnvoiActif.Should().BeFalse();
        }
        finally
        {
            if (System.IO.Directory.Exists(dossier)) System.IO.Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public async Task En_partie_le_plafond_suit_le_ping_mesure()
    {
        // Le « jeu » est le processus de test lui-meme : il tourne, donc le plafond se pose.
        var jeu = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
        var helper = Helper(Result.Ok());
        // RenewLease est une methode par defaut de l'interface : le substitut ne la route pas vers
        // ExecuteAsync et rendrait null.
        helper.RenewLease(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        // Le tour de 5 s de la boucle, court ici : l'attente du test ne dépend plus de la charge du runner.
        vm.TourDuPlafond = TimeSpan.FromMilliseconds(50);
        // Le jeu est vivant par la couture, pas par une vraie recherche de processus (CI rouge du 07/10).
        vm.JeuVivant = exe => exe == jeu;
        // 07/10/2026 (CI rouge 37614885766, même trace avec la couture) : le mode démarre mais aucun battement ne tourne en
        // 34 s. Hypothèse, non prouvée : la boucle part par Task.Run sur le pool de .NET, que d'autres tests bloquent en
        // parallèle sur le runner, alors que xUnit déroule ce test sur ses propres fils. Un minimum de fils plus haut
        // retire cette famine ; si le rouge revient, l'hypothèse est fausse.
        System.Threading.ThreadPool.GetMinThreads(out var fils, out var es);
        System.Threading.ThreadPool.SetMinThreads(Math.Max(fils, 64), es);
        // Fenetre deterministe : repos 8 ms, envoi au plafond et ping +22 ms => le plafond doit descendre.
        vm.CollecteurFenetre = (_, _) => Task.FromResult<IReadOnlyList<DrDxt.Measurement.Network.EchantillonEnvoi>>(
            Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(1, 8))
                .Concat(Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(20, 30)))
                .ToList());
        var journal = Substitute.For<IDiagnosticsLog>();
        vm.JournalPlafond = journal;
        vm.RetenirMesureEnvoi(20_000_000, jeu);
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        try
        {
            // La boucle attend 5 s tant que le plafond n'est pas pose, puis une fenetre : deux tours sur un runner charge depassent 10 s.
            var limite = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < limite && !helper.ReceivedCalls().Any(c =>
                       c.GetArguments()[0] is ApplyUploadCapOp { CapBitsPerSecond: 16_000_000 }))
                await Task.Delay(50);
            await helper.Received().ExecuteAsync(
                Arg.Is<PrivilegedOp>(o => o is ApplyUploadCapOp && ((ApplyUploadCapOp)o).CapBitsPerSecond == 16_000_000),
                Arg.Any<CancellationToken>());
            // La trace de l'essai reel : le changement et les deux p90 qui l'ont decide.
            limite = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < limite && !journal.ReceivedCalls().Any()) await Task.Delay(50);
            journal.Received().Change(ScanViewModel.IdJournalPlafond, "20 Mbit/s",
                "16 Mbit/s (ping p90 repos 8 ms, en charge 30 ms)", "DrDXT");
        }
        finally
        {
            await vm.DesactiverPlafondEnvoiAsync();
        }
    }

    // 28/09/2026 : service en marche, l'App n'arme pas son mode et l'ajustement ne tournait plus : le plafond du
    // service restait figé à la valeur du scan. DrDXT ouvert, le plafond suit encore le ping, par le service.
    [Fact]
    public async Task Service_en_marche_le_plafond_suit_encore_le_ping_mesure()
    {
        var jeu = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.ServiceDemarrageActif = () => true;
        vm.CollecteurFenetre = (_, _) => Task.FromResult<IReadOnlyList<DrDxt.Measurement.Network.EchantillonEnvoi>>(
            Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(1, 8))
                .Concat(Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(20, 30)))
                .ToList());
        vm.HelperSansLancer = helper;
        vm.RetenirMesureEnvoi(20_000_000, jeu);
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        try
        {
            var limite = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < limite && !helper.ReceivedCalls().Any(c =>
                       c.GetArguments()[0] is RetainServiceUploadCapOp { CapBitsPerSecond: 16_000_000 }))
                await Task.Delay(50);
            await helper.Received().ExecuteAsync(
                Arg.Is<PrivilegedOp>(o => o is RetainServiceUploadCapOp && ((RetainServiceUploadCapOp)o).CapBitsPerSecond == 16_000_000),
                Arg.Any<CancellationToken>());
            await helper.DidNotReceive().ExecuteAsync(Arg.Is<PrivilegedOp>(o => o is ApplyUploadCapOp), Arg.Any<CancellationToken>());
        }
        finally
        {
            await vm.DesactiverPlafondEnvoiAsync();
        }
    }

    // 28/09/2026 : l'ajustement en mode service passait par le helper qui se lance à la demande, donc une invite
    // UAC en pleine partie, répétée toutes les 20 s si elle était refusée. Il ne passe plus que par un client qui
    // ne lance jamais le helper ; sans lui, le plafond du service reste celui retenu.
    [Fact]
    public async Task Service_en_marche_l_ajustement_ne_lance_jamais_le_helper()
    {
        var jeu = System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
        var helper = Helper(Result.Ok());
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.ServiceDemarrageActif = () => true;
        vm.FenetreAjustement = TimeSpan.Zero;
        vm.CollecteurFenetre = (_, _) => Task.FromResult<IReadOnlyList<DrDxt.Measurement.Network.EchantillonEnvoi>>(
            Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(1, 8))
                .Concat(Enumerable.Range(0, 40).Select(_ => new DrDxt.Measurement.Network.EchantillonEnvoi(20, 30)))
                .ToList());
        vm.RetenirMesureEnvoi(20_000_000, jeu);
        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);
        try
        {
            await Task.Delay(1500);
            helper.ReceivedCalls().Count(c => c.GetArguments()[0] is RetainServiceUploadCapOp)
                .Should().Be(1, "seule la bascule, un geste du joueur, passe par le helper qui peut se lancer");
        }
        finally
        {
            await vm.DesactiverPlafondEnvoiAsync();
        }
    }

    [Fact]
    public async Task Un_bail_refuse_laisse_la_protection_eteinte_et_le_dit()
    {
        var helper = Helper(Result.Fail(ErrorCodes.TweakNotApplicable, "UAC refuse"));
        var vm = Vm(Array.Empty<RepairFinding>(), helper);
        vm.RetenirMesureEnvoi(20_000_000, JeuAbsent);

        await vm.BasculerPlafondEnvoiCommand.ExecuteAsync(null);

        vm.PlafondEnvoiActif.Should().BeFalse();
        vm.ErreurPlafondEnvoi.Should().Contain("UAC refuse");
        await helper.DidNotReceive().ExecuteAsync(
            Arg.Is<PrivilegedOp>(o => o is ApplyUploadCapOp), Arg.Any<CancellationToken>());
    }

    // ── 25/09/2026 : réparer Windows (DISM puis sfc, console visible) ─────────────────────────
    private static readonly RepairFinding MagasinReparable = new(
        "STABILITE", "magasin de composants", "abîmé, réparable", "sain", RepairStatus.Suspicious,
        "DISM /CheckHealth signale des composants réparables.", RepairAction.Recommandation);

    [Fact]
    public async Task Un_magasin_reparable_arme_la_carte_et_le_maintien_ouvre_la_console()
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        System.Diagnostics.ProcessStartInfo? lance = null;
        vm.LancerConsole = psi => { lance = psi; return new TaskCompletionSource().Task; };
        await vm.ScanCommand.ExecuteAsync(null);

        vm.ReparationVisible.Should().BeTrue();
        vm.CarteWindowsVisible.Should().BeTrue();
        vm.LancerReparationCommand.Execute(null);

        lance.Should().NotBeNull();
        lance!.Verb.Should().Be("runas");
        vm.ReparationLancee.Should().BeTrue();
        vm.ReparationAOuvrir.Should().BeFalse("une console suffit : le bouton ne la rouvre pas");
    }

    // 29/09/2026, critique B10 (passage 3) : la console fermée, rien ne revenait dans DrDXT ; le joueur ne savait
    // pas si la réparation avait marché. À la fermeture, l'état de Windows est relu (lecture) et la carte le dit.
    [Theory]
    [InlineData("sain", RepairStatus.ExpectedDefault, "Reparation.Windows.Terminee.Sain", false)]
    [InlineData("abîmé, réparable", RepairStatus.Suspicious, "Reparation.Windows.Terminee.Abime", true)]
    [InlineData(null, RepairStatus.Unknown, "Reparation.Windows.Terminee.NonRelu", true)]
    public async Task La_console_fermee_la_carte_dit_ce_que_la_relecture_trouve(string? valeur, RepairStatus statut, string attendu, bool relancable)
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        var console = new TaskCompletionSource();
        vm.LancerConsole = _ => console.Task;
        vm.RelireSanteWindows = () => new RepairFinding("STABILITE", "magasin de composants", valeur, "sain", statut, "relu par DISM /CheckHealth");
        await vm.ScanCommand.ExecuteAsync(null);
        vm.LancerReparationCommand.Execute(null);

        vm.EtatReparation.Should().Be("Reparation.Windows.Lancee", "témoin : pendant la réparation, l'état en cours");
        console.SetResult();
        await vm.FinDeReparation!;

        vm.EtatReparation.Should().Be(attendu);
        vm.ReparationAOuvrir.Should().Be(relancable);
    }

    // 30/09/2026 (boucle « la carte, prouvée », G3) : au lancement, une preuve du repos commencée avant un redémarrage se
    // conclut, et la carte dit ce que la mesure a décidé. Rien n'attendait : aucune phrase.
    [Theory]
    [InlineData(14.0, "Carte.Repos.Prouve")]
    [InlineData(55.0, "Carte.Repos.PasDeGain")]
    public async Task Au_lancement_la_preuve_du_repos_se_conclut_et_se_dit(double apresW, string cle)
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
        var attente = new DrDxt.Application.Tweaks.PreuvesEnAttente(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}.json"));
        attente.Garder(new("gpu.repos_global_normal", "inverse", null, [55.0, 55.5, 56.0]));
        var sonde = Substitute.For<IHardwareProbe>();
        sonde.Lire().Returns(new HardwareReading(apresW, null));

        await vm.ConclureReposCarteAsync(levier, sonde, attente, _ => Task.CompletedTask);

        vm.VerdictReposCarte.Should().StartWith(cle);
        (await Task.FromResult(attente.Charger())).Should().BeNull();
    }

    // 30/09/2026 (G4) : un réglage gardé au repos reste sous surveillance en jeu. Trois parties d'après, toutes sous les
    // trois d'avant sur le même jeu : il est retiré et c'est dit. Pas encore trois : rien ne bouge.
    [Theory]
    [InlineData(80.0, true)]
    [InlineData(91.0, false)]
    public async Task Un_reglage_garde_qui_abime_le_jeu_est_retire(double low1Apres, bool retire)
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
        var le = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var retenu = new DrDxt.Application.Tweaks.ReglagesRetenus(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retenu-{Guid.NewGuid():N}.json"));
        retenu.Garder(new("gpu.repos_global_normal", "inverse", null, le));
        DrDxt.Application.Parties.PartieRelevee P(int j, double l) =>
            new("cod", le.AddDays(j), le.AddDays(j).AddMinutes(20), 10, 140, l, BottleneckState.CpuBoundStrong);
        var parties = new[] { P(-3, 90), P(-2, 92), P(-1, 91), P(1, low1Apres), P(2, low1Apres + 1), P(3, low1Apres + 0.5) };

        await vm.SurveillerLaCarteEnJeuAsync(levier, parties, retenu);

        if (retenu.Charger() is null != retire) throw new Xunit.Sdk.XunitException("le retenu doit disparaître si et seulement si le réglage est retiré");
        if (retire)
        {
            await levier.Received(1).RevertAsync(Arg.Any<TweakSnapshot>());
            vm.VerdictReposCarte.Should().StartWith("Carte.Repos.PireEnJeu");
        }
        else await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
    }

    // 30/09/2026 (G5) : le geste sur la page Carte graphique. Maintenir mesure le repos, écrit « Normal » et garde la
    // preuve ; la carte dit qu'elle se jugera au prochain redémarrage. Un levier qui refuse : rien d'attendu, et dit.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Maintenir_mesure_ecrit_et_met_la_preuve_en_attente(bool ecrit)
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.ApplyAsync().Returns(ecrit
            ? Result<TweakSnapshot>.Ok(new TweakSnapshot("gpu.repos_global_normal", "inverse"))
            : Result<TweakSnapshot>.Fail(ErrorCodes.TweakNotApplicable, "non"));
        var sonde = Substitute.For<IHardwareProbe>();
        sonde.Lire().Returns(new HardwareReading(55.8, null));
        var attente = new DrDxt.Application.Tweaks.PreuvesEnAttente(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}.json"));
        vm.LevierReposCarte = levier;
        vm.SondeCarte = sonde;
        vm.AttenteReposCarte = attente;
        vm.AttendreCarte = _ => Task.CompletedTask;

        await vm.RendreLeReposALaCarteCommand.ExecuteAsync(null);

        vm.ReposCarteEnAttente.Should().Be(ecrit);
        (attente.Charger() is not null).Should().Be(ecrit);
        if (!ecrit) vm.ErreurReposCarte.Should().Be("Carte.Repos.Refuse");
        vm.ReposCarteEnCours.Should().BeFalse();
    }

    [Fact]
    public async Task Sans_preuve_en_attente_rien_ne_se_dit()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var levier = Substitute.For<DrDxt.Core.ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");

        await vm.ConclureReposCarteAsync(levier, Substitute.For<IHardwareProbe>(),
            new DrDxt.Application.Tweaks.PreuvesEnAttente(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}.json")), _ => Task.CompletedTask);

        vm.VerdictReposCarte.Should().BeNull();
    }

    [Fact]
    public void Copier_met_la_commande_dans_le_presse_papiers_et_rien_d_autre()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var copies = new List<string>();
        vm.VersPressePapiers = copies.Add;

        vm.CopierCommand.Execute("icacls \"C:\\x.exe\" /reset");
        vm.CopierCommand.Execute("");

        copies.Should().Equal("icacls \"C:\\x.exe\" /reset");
    }

    // 29/09/2026, critique B10 (passage 3) : rien ne disait de quand dataient les points affichés.
    [Fact]
    public async Task Le_resultat_dit_l_heure_du_scan()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        vm.Maintenant = () => new DateTime(2026, 9, 29, 11, 24, 0);
        vm.DateDuScan.Should().BeNullOrEmpty("témoin : avant le scan, pas de date");

        await vm.ScanCommand.ExecuteAsync(null);

        vm.DateDuScan.Should().Be("Scan.Date 29/09 · 11:24");
    }

    [Fact]
    public async Task Sans_constat_reparable_la_carte_n_existe_pas()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.ReparationVisible.Should().BeFalse();
        vm.CarteWindowsVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Une_fenetre_UAC_refusee_le_dit_sans_panne()
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        vm.LancerConsole = _ => throw new System.ComponentModel.Win32Exception(1223);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.LancerReparationCommand.Execute(null);
        vm.ReparationLancee.Should().BeFalse();
        vm.ErreurReparation.Should().Be("Reparation.Windows.Refusee");
    }

    // 04/10/2026 : vu sur la machine de référence, RestoreHealth échoue (0x800f0915) et ce qui est abîmé n'est que des versions
    // remplacées. Relancer la même réparation tournait en rond : la carte le dit et propose le nettoyage.
    [Fact]
    // DOC-WIN (09/10/2026, le mainteneur : DISM /ResetBase non, « un meilleur moyen de gérer les problèmes Windows ») :
    // même quand tout ce qui est abîmé est une version remplacée, le geste est la réinstallation de Microsoft (elle garde
    // fichiers, applications et réglages), jamais le nettoyage irréversible.
    public async Task Une_reparation_sans_source_ouvre_toujours_la_reinstallation_jamais_ResetBase()
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        var lances = new List<System.Diagnostics.ProcessStartInfo>();
        var console = new TaskCompletionSource();
        vm.LancerConsole = psi => { lances.Add(psi); return psi.FileName.StartsWith("ms-settings") ? null : console.Task; };
        vm.RelireSanteWindows = () => new RepairFinding("STABILITE", "magasin de composants", "abîmé, réparable", "sain", RepairStatus.Suspicious, "relu");
        await vm.ScanCommand.ExecuteAsync(null);
        vm.CorruptionSansSource = () => DrDxt.App.Platform.CorruptionDuMagasin.Choisir(
            DrDxt.App.Platform.CorruptionDuMagasin.Lire(CbsSansSource.Split('\n')));
        vm.LancerReparationCommand.Execute(null);
        console.SetResult();
        await vm.FinDeReparation!;

        vm.LabelReparationTitre.Should().Be("Reparation.Windows.Reinstallation.Titre");
        vm.LancerReparationCommand.Execute(null);
        lances.Should().NotContain(p => p.Arguments.Contains("ResetBase"));
        lances[^1].FileName.Should().Be("ms-settings:recovery");
    }

    [Fact]
    public async Task Une_reparation_sans_source_sur_des_composants_en_service_ouvre_la_reinstallation_de_Microsoft()
    {
        var vm = Vm(new[] { MagasinReparable }, helper: null);
        var lances = new List<System.Diagnostics.ProcessStartInfo>();
        var console = new TaskCompletionSource();
        vm.LancerConsole = psi => { lances.Add(psi); return psi.FileName.StartsWith("ms-settings") ? null : console.Task; };
        vm.RelireSanteWindows = () => new RepairFinding("STABILITE", "magasin de composants", "abîmé, réparable", "sain", RepairStatus.Suspicious, "relu");
        await vm.ScanCommand.ExecuteAsync(null);
        vm.CorruptionSansSource = () => DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reinstaller;
        vm.LancerReparationCommand.Execute(null);
        console.SetResult();
        await vm.FinDeReparation!;

        vm.EtatReparation.Should().Be("Reparation.Windows.Terminee.Reinstaller");
        vm.LabelReparationTitre.Should().Be("Reparation.Windows.Reinstallation.Titre");
        vm.LancerReparationCommand.Execute(null);
        lances[^1].FileName.Should().Be("ms-settings:recovery", "relancer la même réparation ne changerait rien");
        vm.EtatReparation.Should().Be("Reparation.Windows.Reinstallation.Ouverte");
    }

    private const string CbsSansSource = """
        2026-10-04 04:59:09, Info  CBS    (p)	CSI Payload Corrupt	(n)			amd64_userexperience-oobe_31bf3856ad364e35_10.0.26100.1591_none_54983d2d7a4908c4\OOBE\a.js
        2026-10-04 04:59:09, Info  CBS    (p)	CSI Payload Corrupt	(n)			amd64_windowssearchengine.resources_31bf3856ad364e35_7.0.26100.1455_fr-fr_ee5c163ff327bb7f\b.dll
        2026-10-04 04:59:10, Info  CBS    Total Detected Corruption:	2
        2026-10-04 04:59:10, Info  CBS    Failed to run Detect and repair. [HRESULT = 0x800f0915 - CBS_E_REPAIR_CONTENT_MISSING]
        """;

    // Revue du 04/10 (P0) : une ligne « Corrupt » d'un autre format (fichier en service au hash faux), ou un compte
    // qui ne retombe pas sur le total écrit par CBS, ne doit jamais mener au nettoyage irréversible.
    [Fact]
    public void Une_lecture_incomplete_du_journal_CBS_garde_la_reparation()
    {
        var winsxs = new[]
        {
            "amd64_userexperience-oobe_31bf3856ad364e35_10.0.26100.9278_none_54b61c157a331ee2",
            "amd64_windowssearchengine.resources_31bf3856ad364e35_7.0.26100.9278_fr-fr_0000000000000000",
        };
        var complet = DrDxt.App.Platform.CorruptionDuMagasin.Lire(CbsSansSource.Split('\n'));
        DrDxt.App.Platform.CorruptionDuMagasin.Choisir(complet)
            .Should().Be(DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reinstaller, "témoin : lecture complète, source introuvable");

        var autreFormat = CbsSansSource.Replace("Total Detected Corruption:\t2",
            "(p)\tCSI Payload File Corrupt\t0x00000000\tsvchost.exe\tamd64_x\tHashes for file member do not match\n"
            + "2026-10-04 04:59:10, Info  CBS    Total Detected Corruption:\t3");
        var lu = DrDxt.App.Platform.CorruptionDuMagasin.Lire(autreFormat.Split('\n'));
        lu.Complete.Should().BeFalse();
        DrDxt.App.Platform.CorruptionDuMagasin.Choisir(lu).Should().Be(DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reparer);

        var compteFaux = CbsSansSource.Replace("Total Detected Corruption:\t2", "Total Detected Corruption:\t5");
        DrDxt.App.Platform.CorruptionDuMagasin.Lire(compteFaux.Split('\n')).Complete.Should().BeFalse("2 lues sur 5 annoncées");
    }

    [Fact]
    public void Le_journal_CBS_nomme_les_composants_abimes_et_la_source_manquante()
    {
        var lu = DrDxt.App.Platform.CorruptionDuMagasin.Lire(CbsSansSource.Split('\n'));
        lu.SourceIntrouvable.Should().BeTrue();
        lu.Composants.Should().HaveCount(2);

        DrDxt.App.Platform.CorruptionDuMagasin.Choisir(lu)
            .Should().Be(DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reinstaller);
        DrDxt.App.Platform.CorruptionDuMagasin.Choisir(lu with { SourceIntrouvable = false })
            .Should().Be(DrDxt.App.Platform.CorruptionDuMagasin.Geste.Reparer, "sans échec lu, la réparation reste le geste");
    }

    // 04/10/2026 (plan 1.3) : un réglage BIOS changé entre deux références se note, daté du démarrage du PC.
    [Fact]
    public void Un_reglage_BIOS_change_se_note_date_du_demarrage()
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"changements-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(chemin);
            vm.NoterChangementBios("Intel Default Settings", "Intel Default Settings");
            vm.NoterChangementBios(null, "MSI Performance Settings");
            vm.JournalChangements.Lire().Should().BeEmpty("témoin : rien de changé, ou pas d'avant connu");

            var mesureAvant = DateTimeOffset.Now.AddDays(-3);
            vm.NoterChangementBios("Intel Default Settings", "MSI Performance Settings", mesureAvant);

            var c = vm.JournalChangements.Lire().Should().ContainSingle().Subject;
            c.Libelle.Should().Contain("Changement.Bios");
            var demarrage = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
            c.Quand.Should().BeCloseTo(demarrage, TimeSpan.FromSeconds(5), "le changement a pris effet au plus tard au démarrage");
            c.Depuis.Should().Be(mesureAvant, "et au plus tôt après la dernière mesure sous l'ancien réglage");
            vm.ChangementsEnAttente.Should().ContainSingle(l => l.EnJeu == "Changement.Tableau.SansAvant", "la carte se refait aussitôt, sans partie d'avant (08/10 : en tableau)");
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // 04/10/2026 (plan 2.1) : l'Instant Replay basculé entre deux scans entre au journal, nommé en mots.
    [Fact]
    public void Un_reglage_bascule_entre_deux_scans_entre_au_journal()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"suivi-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            vm.SuiviReglages = new DrDxt.Application.Parties.SuiviDesReglages(System.IO.Path.Combine(dossier, "s.json"));
            var t = DateTimeOffset.Now;
            vm.ReleverReglagesSuivis(new Dictionary<string, string> { ["nvidia.instant_replay"] = "on" }, t);
            vm.JournalChangements.Lire().Should().BeEmpty("témoin : premier relevé");

            vm.ReleverReglagesSuivis(new Dictionary<string, string> { ["nvidia.instant_replay"] = "off" }, t.AddHours(1));

            var c = vm.JournalChangements.Lire().Should().ContainSingle().Subject;
            c.Libelle.Should().Be("Changement.Reglage");
            c.Depuis.Should().Be(t);
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // 04/10/2026 (plan 2.3) : le mode Jeu et HAGS effectif entrent au suivi ; un état non lu n'y entre pas.
    [Fact]
    public void Le_mode_Jeu_et_HAGS_effectif_sont_suivis_quand_ils_sont_lus()
    {
        var st = new SettingsState(TriState.Unknown, TriState.Unknown, "", TriState.Off, TriState.On, null, TriState.Unknown,
            HagsEffective: TriState.On);
        var etats = ScanViewModel.EtatsSuivis(st);
        etats["windows.game_mode"].Should().Be("on");
        etats["windows.hags"].Should().Be("on", "l'état effectif, pas la valeur du registre qui attend un redémarrage");
        ScanViewModel.EtatsSuivis(st with { GameMode = TriState.Unknown }).Should().NotContainKey("windows.game_mode");

        // Plan 2.4 : la fréquence et la tension de la mémoire datent un changement fait au BIOS.
        var ram = ScanViewModel.EtatsSuivis(null, Hw() with { Memory = [new MemoryModule("F4-4000C18-32GTZR", "G.Skill", 3800, 4000, 26, 2, 1350)] });
        ram["memoire.frequence"].Should().Be("3800 MT/s");
        ram["memoire.tension"].Should().Be("1.35 V");
        ScanViewModel.EtatsSuivis(null, Hw() with { Memory = [] }).Should().NotContainKey("memoire.frequence");
    }

    // 04/10/2026 (plan 2.3, règle 4) : un levier de DrDXT moins bien en jeu se retire seul ; un réglage basculé par le
    // joueur (absent du journal des leviers) n'est jamais touché.
    [Fact]
    public async Task Un_levier_de_DrDXT_moins_bien_en_jeu_se_retire_seul()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", "1", "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
            var levier = Substitute.For<ITweak>();
            levier.TakesEffectAfterReboot.Returns(true);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.hagsOn" ? levier : null;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };

            var dits = await vm.RetirerCeQuiCouteAsync(pire);

            dits.Should().ContainSingle().Which.Should().Be("Changement.RetireAuRedemarrage");
            await levier.Received(1).RevertAsync(pose);
            leviers.Lire().Should().BeEmpty("retiré, il n'est plus « gardé par DrDXT »");

            (await vm.RetirerCeQuiCouteAsync(pire)).Should().BeEmpty("déjà retiré : plus au journal des leviers");

            // 06/10/2026 (revue) : retiré de la machine mais resté au manifeste signé : la carte le dit.
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Fail(ErrorCodes.ManifestUnreadable, "illisible"));
            (await vm.RetirerCeQuiCouteAsync(pire)).Should().Equal("Changement.RetireAuRedemarrage", "Changement.PasSortiDuManifeste");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    private sealed class PrioritesFactices : DrDxt.App.Platform.IPrioritesDeProcessus
    {
        public System.Diagnostics.ProcessPriorityClass? Lire(int pid) => null;
        public bool Poser(int pid, System.Diagnostics.ProcessPriorityClass priorite) => false;
    }

    // 07/10/2026 (L-VOISINS) : le calmeur de voisins se juge comme un levier. Sa bascule est datée au journal (visible sur
    // la carte), « Arrêter » reste cliquable pendant qu'il tourne, et un « moins bien en jeu » l'éteint seul ; « mieux »
    // le laisse. Aucun vrai processus : priorités factices, aucun jeu en cours.
    [Theory]
    [InlineData(80, true)]
    [InlineData(110, false)]
    public async Task Le_calmeur_de_voisins_est_date_juge_et_retire_seul_s_il_coute(double low1Apres, bool retire)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"calmeur-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            // Le libellé porte la valeur : sinon l'arrêt, même libellé dans la minute, serait pris pour un doublon.
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, loc: k => k == "Changement.Reglage" ? "{0}:{1}>{2}" : k);
            vm.Priorites = new PrioritesFactices();
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            _ = vm.BasculerCalmerVoisinsCommand.ExecuteAsync(null);

            vm.CalmerVoisinsActif.Should().BeTrue();
            vm.BasculerCalmerVoisinsCommand.CanExecute(null).Should().BeTrue("« Arrêter » se clique pendant qu'il tourne");
            var pose = vm.JournalChangements.Lire().Should().ContainSingle().Which;
            pose.Cle.Should().Be("voisins.calmer:on");
            vm.ChangementsEnAttente.Should().Contain(l => l.EnJeu == "Changement.Tableau.SansAvant", "la bascule se lit sur la carte (pas encore de parties ; 08/10 : en tableau)");

            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", pose.Quand.AddHours(h),
                pose.Quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var parties = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, low1Apres), P(20, low1Apres + 2), P(30, low1Apres + 1) };

            var dits = await vm.RetirerCeQuiCouteAsync(parties);

            vm.CalmerVoisinsActif.Should().Be(!retire);
            if (retire)
            {
                dits.Should().ContainSingle().Which.Should().Be("Changement.Retire");
                vm.JournalChangements.Lire().First().Cle.Should().Be("voisins.calmer:off", "l'arrêt est daté à son tour");
            }
            else
            {
                dits.Should().BeEmpty();
                await vm.BasculerCalmerVoisinsCommand.ExecuteAsync(null);
                vm.CalmerVoisinsActif.Should().BeFalse("annulable depuis l'écran");
                vm.JournalChangements.Lire().First().Cle.Should().Be("voisins.calmer:off");
            }
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // 07/10/2026 (L-VOISINS) : les parties relevées nomment un voisin gourmand : le calmeur est proposé d'office, sans être
    // allumé ; exclu par le joueur, il ne l'est plus, et l'exclusion est gardée.
    [Fact]
    public void Un_voisin_gourmand_des_parties_fait_proposer_le_calmeur_sauf_s_il_est_exclu()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"proposition-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var memoire = new DrDxt.Application.Parties.PartiesMemorisees(System.IO.Path.Combine(dossier, "c.json"), System.IO.Path.Combine(dossier, "p.json"));
            var t = DateTimeOffset.Now.AddHours(-1);
            memoire.Ajouter(new DrDxt.Application.Parties.PartieRelevee("cod.exe", t, t.AddMinutes(20), 6, 120, 90, BottleneckState.CpuBoundOrAppBound,
                Voisins: [new("obs64", 60, 3)]));
            memoire.Eteindre(); // le relevé est allumé par défaut : coupé, aucune veille ne démarre dans le test
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.Priorites = new PrioritesFactices();
            vm.FichierExclus = new FichierJsonAtomique(System.IO.Path.Combine(dossier, "x.json"), "X");
            vm.MemoireParties = memoire;

            vm.ChargerParties();

            vm.CalmeurPropose.Should().BeTrue();
            vm.PropositionCalmer.Should().Be("Voisins.Proposition");
            vm.CalmerVoisinsActif.Should().BeFalse("proposé, jamais allumé sans le clic du joueur");

            vm.VoisinsExclus = "OBS64.exe";
            vm.CalmeurPropose.Should().BeFalse();
            vm.PropositionCalmer.Should().Be("Voisins.Proposition.Aucun");
            System.IO.File.ReadAllText(System.IO.Path.Combine(dossier, "x.json")).Should().Be("OBS64.exe");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // 07/10/2026 (revue F-7) : HAGS posé, deux parties, puis le joueur baisse sa limite au BIOS (B) et tout chute.
    // Jugé sans borne, l'après de HAGS prenait les parties de B et HAGS était annulé à cause du BIOS. Borné comme à
    // l'écran : deux parties seulement, « pas encore jugé », rien n'est retiré.
    [Fact]
    public async Task Le_retrait_automatique_ne_juge_pas_un_levier_sur_les_parties_du_changement_suivant()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-borne-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", "1", "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand.AddHours(25), "PL1", quand.AddHours(24), "cpu.pl1:65"));
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.hagsOn" ? levier : null;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var parties = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 93), P(20, 92), P(30, 80), P(40, 82), P(50, 81) };

            (await vm.RetirerCeQuiCouteAsync(parties)).Should().BeEmpty();
            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // 07/10/2026 (revue F-23) : le retrait part à l'ouverture de l'App, sans geste. Si le helper ne tourne pas déjà,
    // l'annuler ferait surgir une invite UAC que le joueur n'a pas demandée : rien n'est retiré, et la carte dit d'annuler.
    [Fact]
    public async Task Sans_helper_deja_lance_le_retrait_ne_fait_surgir_aucune_invite_et_le_dit()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-uac-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", "1", "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses(["latency.hagsOn"], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "HAGS", quand.AddHours(-1), "windows.hags:on"));
            var levier = Substitute.For<ITweak>();
            levier.RequiresElevation.Returns(true);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.hagsOn" ? levier : null;
            vm.HelperJoignableSansUac = () => Task.FromResult(false);
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };

            (await vm.RetirerCeQuiCouteAsync(pire)).Should().ContainSingle().Which.Should().Be("Changement.ARetirer");
            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
            leviers.Lire().Should().ContainSingle("rien n'est retiré : le bouton Annuler reste là");

            vm.HelperJoignableSansUac = () => Task.FromResult(true);
            (await vm.RetirerCeQuiCouteAsync(pire)).Should().ContainSingle().Which.Should().Be("Changement.Retire", "helper déjà lancé : le retrait part, sans invite");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // 07/10/2026 (revue F-9) : le fichier d'échange remis par DrDXT protège des plantages, il n'annonce aucune image.
    // « Moins bien en jeu » sur trois parties ne le retire pas : l'annuler re-désactiverait le fichier d'échange.
    [Fact]
    public async Task Le_fichier_d_echange_pose_par_DrDXT_ne_se_retire_pas_sur_les_images()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-pf-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            const string Mm = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
            var registre = Substitute.For<DrDxt.Application.Tweaks.Hardware.IRegistryReader>();
            registre.ReadMultiString(RegistryHiveKind.LocalMachine, Mm, "PagingFiles").Returns(Result<string?>.Ok(""));
            var helper = Substitute.For<IElevatedClient>();
            helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Result.Ok()));
            var levier = new DrDxt.Application.Tweaks.FixIt.SanePagefileTweak(registre, helper);
            var pose = (await levier.ApplyAsync()).Value!;

            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(levier.Id, true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var quand = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
            vm.NoterPoses([levier.Id], quand.AddHours(-2));
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(quand, "PF", quand.AddHours(-1), "memoire.fichier_echange:on"));
            vm.LevierParId = id => id == levier.Id ? levier : null;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", quand.AddHours(h), quand.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };
            helper.ClearReceivedCalls();

            (await vm.RetirerCeQuiCouteAsync(pire)).Should().BeEmpty();
            await helper.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
            leviers.Lire().Should().ContainSingle();
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // 06/10/2026 (plan 4.4) : un réglage posé par DrDXT il y a des jours s'annule depuis l'écran. Avant, « Annuler »
    // n'existait que juste après un réglage de carte graphique ; le reste ne partait que par le retrait automatique.
    [Fact]
    public async Task Un_reglage_pose_par_DrDXT_s_annule_depuis_l_ecran()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.vrrFlipModelOn", "origine", "posee");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.vrrFlipModelOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = id => id == "latency.vrrFlipModelOn" ? levier : null;
            var sortis = new List<string>();
            vm.RetirerDuManifeste = id => { sortis.Add(id); return Task.FromResult(Result.Ok()); };

            vm.ChargerLeviersPoses();
            vm.LeviersPoses.Should().ContainSingle().Which.Id.Should().Be("latency.vrrFlipModelOn");

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.vrrFlipModelOn");

            await levier.Received(1).RevertAsync(pose);
            sortis.Should().Equal("latency.vrrFlipModelOn");
            leviers.Lire().Should().BeEmpty("annulé, il n'est plus « posé par DrDXT »");
            vm.LeviersPoses.Should().BeEmpty();
            vm.MessageLeviers.Should().Be("Leviers.Annule");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // 06/10/2026 : après une désinstallation puis une réinstallation, le journal des leviers disait encore « posé » alors
    // que la valeur était revenue à l'origine. La carte ne montre que ce qui est encore en place.
    [Fact]
    public void Un_reglage_revenu_a_sa_valeur_d_avant_n_est_pas_montre_comme_pose()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            const string chemin = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
            string Op(string v) => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, chemin,
                "HistoricalCaptureEnabled", RegistryValueType.DWord, v)).Value!;
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.gameDvrBackgroundOff", Op("1"), Op("0"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(pose.TweakId, true, false, pose, null)], null));
            var registre = Substitute.For<DrDxt.Application.Tweaks.Hardware.IRegistryReader>();
            registre.ReadDword(RegistryHiveKind.CurrentUser, chemin, "HistoricalCaptureEnabled").Returns(Result<uint?>.Ok(1u));

            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers, registreGains: registre);
            vm.ChargerLeviersPoses();

            vm.LeviersPoses.Should().BeEmpty("la valeur est celle d'avant DrDXT : rien à annuler");

            registre.ReadDword(RegistryHiveKind.CurrentUser, chemin, "HistoricalCaptureEnabled").Returns(Result<uint?>.Ok(0u));
            vm.ChargerLeviersPoses();
            vm.LeviersPoses.Should().ContainSingle("témoin : encore en place, il est montré");

            // Revue du 06/10 : changé depuis par le joueur (ni l'origine, ni la valeur de DrDXT) : pas montré,
            // « Annuler » réécrirait la valeur d'avant par-dessus son choix.
            registre.ReadDword(RegistryHiveKind.CurrentUser, chemin, "HistoricalCaptureEnabled").Returns(Result<uint?>.Ok(2u));
            vm.ChargerLeviersPoses();
            vm.LeviersPoses.Should().BeEmpty();
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Revue du 06/10 (P2) : un levier qui ne prend effet qu'au redémarrage le dit ; un levier introuvable (retiré
    // entre-temps) le dit au lieu de ne rien faire ; le message reste visible après la dernière annulation.
    [Fact]
    public async Task Une_annulation_au_redemarrage_ou_introuvable_le_dit()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.hagsOn", "1", "2");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(pose.TweakId, true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var levier = Substitute.For<ITweak>();
            levier.TakesEffectAfterReboot.Returns(true);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = _ => levier;
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            vm.ChargerLeviersPoses();

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.hagsOn");
            vm.MessageLeviers.Should().Be("Leviers.AnnuleAuRedemarrage");

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.hagsOn");
            vm.MessageLeviers.Should().Be("Leviers.Introuvable");
        }
        finally { System.IO.Directory.Delete(dossier, true); }

        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var carte = System.Text.RegularExpressions.Regex.Match(xaml, "<Border x:Name=\"CarteLeviersPoses\"(.*?)</Border>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        carte.Should().Contain("LeviersPoses", "témoin : la carte existe");
        carte.Should().NotContain("MessageLeviers", "dans la carte, le message disparaîtrait avec le dernier levier annulé");
        xaml.Should().Contain("{Binding MessageLeviers}");
    }

    [Fact]
    public async Task Une_annulation_refusee_ne_retire_rien_et_le_dit()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.vrrFlipModelOn", "origine", "posee");
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.vrrFlipModelOn", true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Fail(ErrorCodes.HelperOpFailed, "UAC refusée"));
            vm.LevierParId = _ => levier;
            var sortis = new List<string>();
            vm.RetirerDuManifeste = id => { sortis.Add(id); return Task.FromResult(Result.Ok()); };
            vm.ChargerLeviersPoses();

            await vm.AnnulerLevierCommand.ExecuteAsync("latency.vrrFlipModelOn");

            sortis.Should().BeEmpty();
            leviers.Lire().Should().ContainSingle();
            vm.LeviersPoses.Should().ContainSingle();
            vm.MessageLeviers.Should().Be("Leviers.PasAnnule");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // Revue du 04/10 (P0) : le retrait ne touche que la bascule née de la pose de DrDXT.
    [Theory]
    [InlineData("joueur")]      // DrDXT pose, le joueur coupe puis rallume lui-même : la dernière bascule est la sienne
    [InlineData("ancienne")]    // une vieille bascule jugée pire, puis une nouvelle pose pas encore suivie d'effet
    public async Task Une_bascule_qui_ne_vient_pas_de_la_pose_n_est_jamais_retiree(string cas)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrait-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome("latency.hagsOn", true, false, new TweakSnapshot("latency.hagsOn", "1", "2"), null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            var t = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
            void Bascule(double h, string v) => vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(t.AddHours(h), v, t.AddHours(h - 0.5), "windows.hags:" + v));
            if (cas == "joueur")
            {
                vm.NoterPoses(["latency.hagsOn"], t.AddHours(-5));
                Bascule(-4, "on"); Bascule(-3, "off"); Bascule(0, "on");
            }
            else
            {
                Bascule(0, "on");                       // le joueur, jugé pire ci-dessous
                vm.NoterPoses(["latency.hagsOn"], t.AddHours(1));
            }
            var levier = Substitute.For<ITweak>();
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = _ => levier;
            DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", t.AddHours(h), t.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
            var pire = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81) };

            (await vm.RetirerCeQuiCouteAsync(pire)).Should().BeEmpty();
            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // 04/10/2026 (plan 3b.2) : la frise rassemble changements, parties et incidents, datés, le plus récent en haut.
    [Fact]
    public async Task L_historique_rassemble_changements_parties_et_incidents()
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"frise-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(chemin);
            var t = DateTimeOffset.Now;
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(t.AddHours(-5), "le BIOS"));
            vm.LireIncidents = () => ([t.AddDays(-3)], []);
            await vm.AfficherHistoriqueAsync([new DrDxt.Application.Parties.PartieRelevee("cod.exe", t.AddHours(-2), t.AddHours(-1), 6, 140, 100, BottleneckState.CpuBoundOrAppBound)]);

            vm.AHistorique.Should().BeTrue();
            vm.LignesHistorique.Should().Equal(["Historique.Bilan"], "plan 3b.5 : le bilan de la semaine en tête");
            // 08/10/2026 (critique UI, défaut 12) : la frise est une liste structurée, son genre donne la couleur du point.
            vm.FriseHistorique.Select(e => e.Genre).Should().Equal(
                DrDxt.Application.Parties.GenreDEvenement.Partie, DrDxt.Application.Parties.GenreDEvenement.Changement,
                DrDxt.Application.Parties.GenreDEvenement.ArretBrutal);
            vm.FriseHistorique[0].Texte.Should().Be("Call of Duty", "le nom du jeu à gauche");
            vm.FriseHistorique[0].Detail.Should().Be("Historique.PartieFps", "les FPS à droite");
            vm.FriseHistorique[1].Texte.Should().Be("le BIOS");
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // 07/10/2026 (D7) : une mise à jour faite sans DrDXT se juge sur les parties d'avant et d'après, bornée par les autres.
    [Fact]
    public void Une_mise_a_jour_de_la_machine_se_juge_en_jeu_bornee_par_les_autres_changements()
    {
        var t = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", t.AddHours(h), t.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
        string L(string k) => k == "Changement.Juge" ? "{0}|{2}|{8}/{9}" : k == "Changement.PasEncore" ? "{0}|attente {2}/{3}" : k;
        var parties = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81), P(50, 120) };
        var pilote = new DrDxt.Detection.Repair.FaitDate(t, "pilote NVIDIA (617.14)");
        var memeMinute = new DrDxt.Detection.Repair.FaitDate(t.AddSeconds(30), "pilote NVIDIA (617.14)");
        // Un levier de DrDXT posé à +40 h : la partie à +50 h n'est plus à mettre au compte du pilote.
        var levier = new DrDxt.Application.Parties.Changement(t.AddHours(40), "levier");

        ScanViewModel.LignesMisesAJour([pilote, memeMinute], parties, [levier], L)
            .Should().Equal("pilote NVIDIA (617.14)|Changement.Pire|3/3");
        ScanViewModel.LignesMisesAJour([pilote], parties.Take(4).ToList(), [], L)
            .Should().Equal("pilote NVIDIA (617.14)|attente 1/3");
        ScanViewModel.LignesMisesAJour([new DrDxt.Detection.Repair.FaitDate(t.AddHours(-40), "trop tôt")], parties, [], L)
            .Should().BeEmpty("aucune partie d'avant : elle ne se jugera jamais");
    }

    [Fact]
    public async Task L_historique_dit_ce_qu_une_mise_a_jour_a_coute_en_jeu()
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"frise-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(chemin);
            var t = DateTimeOffset.Now;
            vm.LireMisesAJourDeLaMachine = () => [new DrDxt.Detection.Repair.FaitDate(t.AddHours(-1), "Windows Update : pilote")];
            await vm.AfficherHistoriqueAsync([new DrDxt.Application.Parties.PartieRelevee("cod.exe", t.AddHours(-3), t.AddHours(-2), 6, 140, 100, BottleneckState.CpuBoundOrAppBound)]);

            vm.LignesHistorique[0].Should().Be("Changement.PasEncore");
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // Revue F-47 : les journaux d'installation ne se relisent pas à chaque fin de partie.
    [Fact]
    public async Task Les_mises_a_jour_de_la_machine_se_lisent_une_fois_par_heure_au_plus()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var t = DateTimeOffset.Now;
        vm.Horloge = () => t;
        var lectures = 0;
        vm.LireMisesAJourDeLaMachine = () => { lectures++; return []; };

        await vm.AfficherHistoriqueAsync([]);
        await vm.AfficherHistoriqueAsync([]);
        lectures.Should().Be(1);
        t = t.AddHours(2);
        await vm.AfficherHistoriqueAsync([]);
        lectures.Should().Be(2);
    }

    // 04/10/2026 (plan 3b.1) : ce qui a changé depuis le dernier scan passe en tête, même sans partie ni changement noté.
    [Fact]
    public async Task L_historique_commence_par_ce_qui_a_change_depuis_le_dernier_scan()
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"frise-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(chemin);
            vm.LireChangementsDepuisLeScan = () => ["pilote changé"];
            await vm.AfficherHistoriqueAsync([]);

            vm.AHistorique.Should().BeTrue();
            vm.LignesHistorique.Should().Equal("Historique.DepuisLeScan");
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // 04/10/2026 (plan 3b.3) : « prêt à jouer » dit les gênes mesurées, puis la ligne de ce qui a été mesuré.
    [Fact]
    public async Task Pret_a_jouer_dit_les_genes_puis_ce_qui_a_ete_mesure()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        vm.MesurerAvantPartie = _ => Task.FromResult(new DrDxt.Application.Parties.EtatAvantPartie(
            [("TiWorker", 80)], 1, 0, 12, 0, null, 16UL << 30));
        await vm.VerifierPretAJouerCommand.ExecuteAsync(null);
        vm.LignesPretAJouer.Should().Equal("PretAJouer.AGenes", "PretAJouer.Voisins", "PretAJouer.Mesure");

        vm.MesurerAvantPartie = _ => Task.FromResult(new DrDxt.Application.Parties.EtatAvantPartie([], 1, 0, 12, 0, 55, 16UL << 30));
        await vm.VerifierPretAJouerCommand.ExecuteAsync(null);
        vm.LignesPretAJouer.Should().Equal("PretAJouer.Ok", "PretAJouer.Mesure");
    }

    [Fact]
    public void La_console_appelle_les_outils_par_leur_chemin_absolu_et_sans_profil()
    {
        var psi = ScanViewModel.ConsoleDeReparation(@"C:\Windows\system32");
        psi.FileName.Should().Be(@"C:\Windows\system32\WindowsPowerShell\v1.0\powershell.exe");
        psi.Arguments.Should().StartWith("-NoProfile ")
            .And.Contain(@"& 'C:\Windows\system32\Dism.exe' /Online /Cleanup-Image /RestoreHealth")
            .And.Contain(@"& 'C:\Windows\system32\sfc.exe' /scannow")
            .And.NotContain("$env:");
    }

    // ── 25/09/2026 : remesurer le processeur a la demande ─────────────────────────────────────
    private sealed class FichierCpu(string? contenu) : DrDxt.Measurement.Cpu.ICpuBaselineFile
    {
        public string? Ecrit { get; private set; }
        public Result WriteAtomic(string json) { Ecrit = json; return Result.Ok(); }
        public Result<string?> Read() => Result<string?>.Ok(contenu);
    }

    private sealed class BancCpu(DrDxt.Measurement.Cpu.CpuBaseline? rendu) : DrDxt.Measurement.Cpu.ICpuSustainedBench
    {
        public int Appels { get; private set; }
        public DrDxt.Measurement.Cpu.CpuBaseline? Mesurer(string machineId) { Appels++; return rendu; }
    }

    private static DrDxt.Measurement.Cpu.CpuBaseline Ref(double rafale, double soutenu, double? watts = null)
        => new("m1", DateTimeOffset.UtcNow, 8, rafale, soutenu, 1.0, watts, watts is null ? null : 5);

    [Fact]
    public async Task Remesurer_remplace_la_phrase_du_processeur_par_la_nouvelle_mesure()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var banc = new BancCpu(Ref(1790, 1606, watts: 66.5));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)));
        await vm.ScanCommand.ExecuteAsync(null);

        banc.Appels.Should().Be(0, "le scan relit la reference existante");
        vm.RemesureCpuVisible.Should().BeTrue();
        await vm.RemesurerProcesseurCommand.ExecuteAsync(null);

        banc.Appels.Should().Be(1);
        vm.RemesureCpuFaite.Should().BeTrue();
        vm.CpuSustainedLabel.Should().Contain("limite de puissance longue");
        // Vu sur la photo du 25/09 : la carte du domaine gardait la phrase de l'ancienne mesure.
        vm.Domaines.Single(d => d.Cle == "processeur").Mesures.Should().Contain(m => m.Contains("limite de puissance longue"));
    }

    [Fact]
    public void La_phrase_du_processeur_nomme_le_reglage_BIOS_lu_quand_la_limite_est_prouvee()
    {
        var limite = new DrDxt.Measurement.Cpu.CpuDeficitSoutenu(10.3, 1.0, true, "Base.", LimiteDePuissance: true);
        var bios = new[] { new BiosSettingReading("CPU Cooler Tuning", 1, "Intel Default Settings", IsDefault: true) };

        ScanViewModel.PhraseProcesseur(limite, bios).Should()
            .Be("Base. Sur ta carte, les limites de puissance laissées sur Auto suivent le réglage BIOS "
                + "« CPU Cooler Tuning », lu ici : « Intel Default Settings ». C'est le profil qu'Intel et MSI "
                + "recommandent pour la stabilité des Core de 13e et 14e génération : DrDXT ne te conseille pas de "
                + "le changer, la puissance en plus se paierait en chaleur et en risque d'instabilité.");
        // Un autre profil est nommé sans la recommandation, qui ne vaut que pour celui d'Intel.
        var autre = new[] { new BiosSettingReading("CPU Cooler Tuning", 2, "MSI Performance Settings", IsDefault: false) };
        ScanViewModel.PhraseProcesseur(limite, autre).Should().EndWith("« MSI Performance Settings ».");
        ScanViewModel.PhraseProcesseur(limite, null).Should().Be("Base.", "sans lecture BIOS, rien n'est ajoute");
        ScanViewModel.PhraseProcesseur(limite with { LimiteDePuissance = false }, bios).Should()
            .Be("Base.", "sans limite prouvee, le reglage n'est pas mis en cause");
        ScanViewModel.PhraseProcesseur(null, bios).Should().BeNull();
        // La date ferme la phrase : le réglage BIOS se place avant elle (vu au rapport réel du 25/09).
        ScanViewModel.PhraseProcesseur(limite with { Lisible = "Base. Mesuré le 25/09/2026." }, bios).Should()
            .EndWith("en risque d'instabilité. Mesuré le 25/09/2026.");
    }

    // 26/09/2026 : le banc processeur charge tous les coeurs rapides plus de quarante secondes. Jeu lance,
    // ni le scan ni le bouton ne le lancent (la reference memoire du 16/09 a ete faussee ainsi).
    [Fact]
    public async Task Un_premier_scan_jeu_lance_ne_lance_pas_le_banc_processeur()
    {
        var fichier = new FichierCpu(null);
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), jeu: 4242);

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Appels.Should().Be(0);
        fichier.Ecrit.Should().BeNull();
        vm.CpuSustainedLabel.Should().BeNull();
        // Pas de ligne, mais la carte, sa raison et son bouton : sans eux, un joueur qui scanne jeu lancé
        // (le parcours normal : le scan capture le jeu) n'obtiendrait jamais de mesure.
        vm.RemesureCpuVisible.Should().BeTrue();
        vm.CpuSauteJeu.Should().BeTrue();
        vm.ErreurRemesureCpu.Should().BeNull("une information, pas une erreur");
    }

    // 26/09/2026 : la référence processeur de la machine de référence (11/09 17:46, ±7,4 %) précède de cinq minutes
    // une capture de Warzone. Écrite avant le garde, elle n'a pas le champ JeuFermeVerifie : la ligne le dit.
    private sealed class JournalCpu : DrDxt.Measurement.IPerformanceJournalFile
    {
        private string? _contenu;
        public Result WriteAtomic(string json) { _contenu = json; return Result.Ok(); }
        public Result<string?> Read() => Result<string?>.Ok(_contenu);
    }

    private static DrDxt.Measurement.PerformanceJournalStore CaptureDansCinqMinutes()
    {
        var store = new DrDxt.Measurement.PerformanceJournalStore(new JournalCpu());
        store.Ajouter(new DrDxt.Core.PerformanceSample(DateTimeOffset.UtcNow.AddMinutes(5), "m1", 164.1, 28, 5, 1.0, "cod"));
        return store;
    }

    [Fact]
    public async Task La_ligne_processeur_dit_quand_sa_reference_a_ete_prise_pendant_une_partie()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)).Replace(",\"JeuFermeVerifie\":false", ""));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            new BancCpu(null), new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), journal: CaptureDansCinqMinutes());

        await vm.ScanCommand.ExecuteAsync(null);

        vm.CpuSustainedLabel.Should().Contain("Cpu.PriseEnJeu");
    }

    // Le rapport est fait pour être envoyé : une baisse de 30 % sans sa réserve y passerait pour un fait.
    [Fact]
    public async Task Le_rapport_porte_la_reserve_de_la_ligne_processeur()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        string? ecrit = null;
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, writeReport: (_, texte) => { ecrit = texte; return "r.txt"; },
            cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
                new BancCpu(null), new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.ExportReportCommand.ExecuteAsync(null);

        ecrit.Should().Contain("en moins qu'au premier instant", "temoin : la phrase du processeur est au rapport")
            .And.Contain("Cpu.PriseEnJeu");
    }

    [Fact]
    public async Task Remesurer_une_reference_prise_en_partie_la_remplace_meme_si_la_remesure_est_bruitee()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var bruitee = new DrDxt.Measurement.Cpu.CpuBaseline("m1", DateTimeOffset.UtcNow, 8, 1000, 1075, 7.4);
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            new BancCpu(bruitee), new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);
        vm.CpuSustainedLabel.Should().Contain("Cpu.PriseEnJeu", "temoin : la reference est bien soupconnee");

        await vm.RemesurerProcesseurCommand.ExecuteAsync(null);

        fichier.Ecrit.Should().NotBeNull("un chiffre qu'on sait faux ne se garde pas contre une mesure honnete");
    }

    [Fact]
    public async Task Une_mesure_processeur_de_cette_version_ne_se_soupconne_pas()
    {
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(new FichierCpu(null))), journal: CaptureDansCinqMinutes());

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Appels.Should().Be(1, "temoin : la reference vient d'etre mesuree, jeu ferme");
        vm.CpuSustainedLabel.Should().NotBeNull().And.NotContain("Cpu.PriseEnJeu");
    }

    [Fact]
    public async Task Remesurer_le_processeur_jeu_lance_est_refuse_et_le_dit()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), jeu: 4242);
        await vm.ScanCommand.ExecuteAsync(null);
        var avant = vm.CpuSustainedLabel;
        avant.Should().NotBeNull("temoin : jeu lance, la reference existante est quand meme relue");

        await vm.RemesurerProcesseurCommand.ExecuteAsync(null);

        banc.Appels.Should().Be(0);
        fichier.Ecrit.Should().BeNull();
        vm.CpuSustainedLabel.Should().Be(avant);
        vm.ErreurRemesureCpu.Should().Be("Cpu.Remesure.JeuEnCours");
    }

    // 27/09/2026 : la remesure sans geste. Une reference suspecte se refait seule quand le poste est au repos.
    private static Func<CancellationToken, Task<(TimeSpan SansSaisie, double? ChargeCpu)>> Poste(TimeSpan sansSaisie, double? charge)
        => _ => Task.FromResult((sansSaisie, charge));

    private static readonly DateTimeOffset Maintenant = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Poste_au_repos_une_reference_suspecte_se_remesure_seule()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);
        vm.CpuSustainedLabel.Should().Contain("Cpu.PriseEnJeu", "temoin : la reference est soupconnee");
        vm.LirePoste = Poste(TimeSpan.FromMinutes(10), 2);

        await vm.TenterRemesureAuReposAsync(Maintenant, CancellationToken.None);

        banc.Appels.Should().Be(1);
        fichier.Ecrit.Should().NotBeNull();
        vm.CpuSustainedLabel.Should().NotContain("Cpu.PriseEnJeu", "la nouvelle reference est prise jeu ferme");

        await vm.TenterRemesureAuReposAsync(Maintenant.AddHours(2), CancellationToken.None);
        banc.Appels.Should().Be(1, "une reference saine ne se remesure plus");
    }

    [Fact]
    public async Task Quelqu_un_au_clavier_rien_ne_se_mesure()
    {
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260))))),
            journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);
        vm.LirePoste = Poste(TimeSpan.FromMinutes(1), 2);

        await vm.TenterRemesureAuReposAsync(Maintenant, CancellationToken.None);

        banc.Appels.Should().Be(0);
    }

    [Fact]
    public async Task Une_remesure_ratee_ne_se_retente_pas_avant_une_heure()
    {
        var banc = new BancCpu(null);
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260))))),
            journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);
        vm.LirePoste = Poste(TimeSpan.FromMinutes(10), 2);

        await vm.TenterRemesureAuReposAsync(Maintenant, CancellationToken.None);
        await vm.TenterRemesureAuReposAsync(Maintenant.AddMinutes(20), CancellationToken.None);
        banc.Appels.Should().Be(1);

        await vm.TenterRemesureAuReposAsync(Maintenant.AddMinutes(61), CancellationToken.None);
        banc.Appels.Should().Be(2);
    }

    [Fact]
    public async Task Une_remesure_ratee_garde_la_phrase_et_le_dit()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            new BancCpu(null), new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)));
        await vm.ScanCommand.ExecuteAsync(null);
        var avant = vm.CpuSustainedLabel;

        await vm.RemesurerProcesseurCommand.ExecuteAsync(null);

        vm.CpuSustainedLabel.Should().Be(avant);
        vm.RemesureCpuFaite.Should().BeFalse();
        vm.ErreurRemesureCpu.Should().Be("Cpu.Remesure.Echec");
        fichier.Ecrit.Should().BeNull();
    }
}
