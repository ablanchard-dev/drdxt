using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>La scène déclarée, l'aperçu d'Optimiser et les leviers rendus ou retirés.</summary>
public sealed partial class ScanApplyCommandTests
{
    // ====================== la scene declaree ======================
    //
    // Du choix a l'ecran jusqu'a la phrase montree, par le vrai chemin : scan, jugement, session,
    // moteur. La ligne « scene declaree » est relue sur le PROFIL rendu par la session, donc sur le
    // contrat qui l'a reellement atteinte.

    private static async Task<Harness> DeclarerPuisOptimiser(SceneRepresentativity scene, string? description)
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Vm.SceneDeclaree = h.Vm.ChoixDeScene.Single(c => c.Valeur == scene);
        h.Vm.DescriptionScene = description;
        await h.Vm.ApplyCommand.ExecuteAsync(null);
        return h;
    }

    // Avant le clic, Optimiser dit ce qu'il va changer : après le scan, l'écran nomme le réglage qu'il essaiera à ce clic. Un
    // seul levier à chaud part en session : l'aperçu ne promet pas le second.
    [Fact]
    public async Task Avant_le_clic_Optimiser_nomme_ce_qu_il_essaiera()
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        h.Vm.ApercuOptimisation.Should().BeNull("rien n'est prévu avant le scan");

        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.ApercuOptimisation.Should().StartWith("Optimiser.Apercu ").And.Contain(TweakDisplayKeys.TitleKey(AChaud[0]))
            .And.NotContain(TweakDisplayKeys.TitleKey(AChaud[1]));
    }

    // Scan sans jeu, puis le joueur lance son jeu et clique Optimiser : lu seulement au scan, le jeu échapperait au veto « l'affichage
    // se règle hors partie », et le mode d'écran changerait sous le jeu, sa question « garder ou revenir » cachée derrière.
    [Fact]
    public async Task Un_jeu_lance_apres_le_scan_empeche_de_changer_l_ecran_au_clic()
    {
        var h = Build(graines: new[] { "fixit.ecran_frequence" });
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Locator!.LocateRunningGamePid().Returns(PidDuJeu);

        await h.Vm.ApplyCommand.ExecuteAsync(null);

        await h.Engine.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default, default, default);
    }

    // Après « Annuler », l'écran gardait « Posé(s) … » et « déjà en place », et un nouveau clic ne reposait rien jusqu'au scan
    // suivant : le levier annulé restait parmi les poses de ce scan.
    [Fact]
    public async Task Apres_annuler_optimiser_peut_reposer_et_ne_dit_plus_pose()
    {
        var dossier = Directory.CreateTempSubdirectory("drdxt-annuler-").FullName;
        try
        {
            const string hags = "latency.hagsOn";
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(Path.Combine(dossier, "leviers.json"));
            var h = Build(graines: new[] { hags }, journalLeviers: leviers,
                applyResult: Result<ApplyReport>.Ok(new ApplyReport([new ApplyOutcome(hags, true, false,
                    new TweakSnapshot(hags, DrDxt.Tests.Shared.InversesDeTest.Pour(hags, "1"), DrDxt.Tests.Shared.InversesDeTest.Pour(hags, "2")), null)], null)));
            await ScanThenApply(h);
            leviers.Lire().Should().ContainSingle("témoin : HAGS est posé et inscrit");
            var levier = Substitute.For<ITweak>();
            levier.Id.Returns(hags);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            h.Vm.LevierParId = id => id == hags ? levier : null;

            await h.Vm.AnnulerLevierCommand.ExecuteAsync(hags);

            h.Vm.ResultatOptimisation.Should().BeNullOrEmpty("le compte rendu d'Optimiser ne dit plus vrai");
            h.Vm.RienAPoserSeul.Should().BeFalse();
            await h.Vm.ApplyCommand.ExecuteAsync(null);
            await EngineReceived(h, 2);
        }
        finally { Directory.Delete(dossier, true); }
    }

    // Les leviers au redémarrage partent sans mesure : l'aperçu promettait « mesuré en jeu » pour eux aussi.
    [Fact]
    public async Task L_apercu_ne_promet_pas_de_mesure_aux_leviers_poses_sans_mesure()
    {
        var h = Build(graines: AuRedemarrage);

        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.ApercuOptimisation.Should().StartWith("Optimiser.Apercu.SansMesure ")
            .And.Contain(TweakDisplayKeys.TitleKey(AuRedemarrage[0])).And.NotContain("Optimiser.Apercu ");
    }

    // « Rien à changer automatiquement ici » s'affichait juste au-dessus de « DrDXT
    // essaie lui-même : … ». Quand le jugement ne retient rien, Optimiser ne pose rien : l'aperçu se tait, la phrase seule dit.
    [Fact]
    public async Task Quand_rien_n_est_a_poser_l_apercu_ne_promet_rien()
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Vm.ApercuOptimisation.Should().NotBeNull("témoin : il y a des réglages à nommer");
        var annonces = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => annonces.Add(e.PropertyName);

        h.Vm.RienAPoserSeul = true;

        h.Vm.ApercuOptimisation.Should().BeNull();
        annonces.Should().Contain(nameof(h.Vm.ApercuOptimisation), "l'écran doit se mettre à jour");
    }

    // L'aperçu promettait le VRR sans dire qu'il n'est pas encore validé sur une
    // vraie machine, alors que la carte Latence le dit. L'aperçu porte la même réserve.
    [Fact]
    public void L_apercu_dit_quand_un_reglage_n_est_pas_encore_valide()
    {
        static DrDxt.App.Profiles.ProfileRecommendationItem Item(string id, bool teste) =>
            new(id, default, "c", "s", default, default, teste, true, null);

        var apercu = ScanViewModel.ApercuDe(["a", "b"], [Item("a", true), Item("b", false)], id => "T-" + id, "Essaiera :", "Sans validation :", "Aucune validation.");

        // Photo de l'accueil : « Optimisations pour les jeux en fenêtre (VRR) pas encore validé », la
        // réserve collée au titre s'accordait mal. Elle devient une phrase à part, sans participe à accorder.
        apercu.Should().Be("Essaiera : T-a, T-b. Sans validation : T-b.");
        ScanViewModel.ApercuDe(["a"], [Item("a", true)], id => "T-" + id, "Essaiera :", "Sans validation :", "Aucune validation.")
            .Should().Be("Essaiera : T-a", "témoin : tout validé, pas de réserve");

        // Un seul réglage, sans validation : le répéter derrière la réserve le dirait deux fois de suite.
        ScanViewModel.ApercuDe(["b"], [Item("b", false)], id => "T-" + id, "Essaiera :", "Sans validation :", "Aucune validation.")
            .Should().Be("Essaiera : T-b. Aucune validation.");
    }

    // Critique de l'app : le sélecteur de scène revenait en entier avant Optimiser, déjà répondu
    // avant le scan. Il se replie en une ligne qui dit le choix retenu, et suit ce choix.
    [Fact]
    public void Avant_Optimiser_la_scene_retenue_se_dit_en_une_ligne_et_suit_le_choix()
    {
        var vm = Build().Vm;
        var partie = vm.ChoixDeScene.Single(c => c.Valeur == SceneRepresentativity.Gameplay);
        var suivis = new List<string?>();
        vm.PropertyChanged += (_, e) => suivis.Add(e.PropertyName);

        vm.SceneDeclaree = partie;

        vm.LabelSceneRetenue.Should().Contain(partie.Libelle);
        suivis.Should().Contain(nameof(vm.LabelSceneRetenue), "la ligne suit un changement de choix");
    }

    // Photo de l'accueil : « Pendant la mesure en jeu, tu joues : Je ne sais pas ». Dans le
    // sélecteur c'est une réponse ; dans la phrase, c'est un aveu qui n'a pas de sens. Pas de choix = on le dit.
    [Fact]
    public void Sans_scene_choisie_la_ligne_le_dit_au_lieu_de_faire_parler_le_joueur()
    {
        var vm = Build().Vm;
        vm.SceneDeclaree = vm.ChoixDeScene.Single(c => c.Valeur == SceneRepresentativity.Unknown);

        vm.LabelSceneRetenue.Should().Be("Scan.Scene.PasChoisie");
    }

    [Fact]
    public void Par_defaut_la_scene_est_JE_NE_SAIS_PAS_et_c_est_le_premier_choix()
    {
        var vm = Build().Vm;

        vm.SceneDeclaree.Valeur.Should().Be(SceneRepresentativity.Unknown, "le silence ne vaut jamais « partie reelle »");

        // La liste se deduit de l'enum : ecrite a la main dans le produit comme dans le test, les deux
        // diraient la meme chose et le test ne verrait pas une scene manquante. Ajouter un membre sans
        // lui donner de case a l'ecran rougit ici.
        vm.ChoixDeScene.Select(c => c.Valeur).Should().BeEquivalentTo(
            Enum.GetValues<SceneRepresentativity>(),
            "une scene que le produit connait mais qu'il ne propose pas est une scene indeclarable");
        vm.ChoixDeScene.Select(c => c.Libelle).Should().OnlyHaveUniqueItems()
            .And.NotContain(string.Empty);
        vm.ChoixDeScene[0].Valeur.Should().Be(SceneRepresentativity.Unknown, "le defaut reste l'aveu d'ignorance");
        vm.DescriptionScene.Should().BeNull();
    }

    [Fact]
    public async Task Une_partie_reelle_declaree_arrive_a_l_ecran_avec_la_reserve_de_DECOUVERTE()
    {
        var h = await DeclarerPuisOptimiser(SceneRepresentativity.Gameplay, "  partie reelle, zone calme  ");

        h.Vm.ResultatOptimisation.Should()
            .Contain("Scan.Scene.Declared Scan.Scene.Gameplay : partie reelle, zone calme",
                "le contrat recu par la session, relu sur son profil, description comprise")
            .And.Contain("trouvé en scène de découverte : à revalider",
                "le role reste Decouverte : aucune validation n'est fabriquee")
            .And.NotContain("personne n'a dit ce qui était joué");
    }

    [Fact]
    public async Task JE_NE_SAIS_PAS_reste_une_scene_inconnue_et_l_ecran_le_dit()
    {
        var h = await DeclarerPuisOptimiser(SceneRepresentativity.Unknown, "je crois une partie");

        h.Vm.ResultatOptimisation.Should()
            .Contain("Scan.Scene.Declared Scan.Scene.Unknown")
            .And.Contain("personne n'a dit ce qui était joué")
            .And.NotContain("à revalider", "une description libre ne transforme pas « je ne sais pas » en partie");
    }

    [Theory]
    [InlineData(SceneRepresentativity.Entrainement, "terrain d'entraînement")]
    [InlineData(SceneRepresentativity.MenuLobby, "mesure dans un menu ou un lobby")]
    // « je n'etais pas en jeu » doit arriver a l'ecran avec sa phrase : la machine
    // etait au repos, donc ce qui est lu est une configuration, pas un ressenti de partie.
    [InlineData(SceneRepresentativity.HorsJeu, "aucun jeu ne tournait pendant la mesure")]
    public async Task Chaque_scene_declaree_porte_SA_reserve_jusqu_a_l_ecran(SceneRepresentativity scene, string reserve)
    {
        var h = await DeclarerPuisOptimiser(scene, null);

        h.Vm.ResultatOptimisation.Should().Contain(reserve).And.NotContain("personne n'a dit ce qui était joué");
    }

    [Fact]
    public async Task Le_levier_mesure_en_jeu_laisse_sa_ligne_au_journal_des_candidats()
    {
        // Du bouton Optimiser jusqu'au disque, par le vrai chemin : scan, jugement, session, moteur.
        // Aucune ligne posee a la main — seul le dossier est temporaire.
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"drdxt-candidats-app-{Guid.NewGuid():N}");
        var chemin = System.IO.Path.Combine(dossier, DrDxt.Application.Autopilot.JournalDesCandidats.NomFichier);
        try
        {
            var h = Build(graines: AChaud, pid: PidDuJeu, nomDuJeu: "Warzone",
                journal: new DrDxt.Application.Autopilot.JournalDesCandidats(chemin, () => "machine-app"));
            await ScanThenApply(h);

            var lignes = System.IO.File.ReadAllLines(chemin);
            lignes.Should().ContainSingle("un seul levier a chaud part en session, donc une seule ligne");
            var ligne = System.Text.Json.JsonSerializer.Deserialize<DrDxt.Application.Autopilot.CandidatJuge>(
                lignes[0], new System.Text.Json.JsonSerializerOptions
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                })!;
            ligne.Levier.Should().Be("fixit.power_plan");
            ligne.MachineId.Should().Be("machine-app");
            ligne.Jeu.Should().Be("Warzone", "le jeu vient du meme localisateur que le journal de performance");
        }
        finally
        {
            try { System.IO.Directory.Delete(dossier, recursive: true); } catch { /* dossier du test */ }
        }
    }

    [Fact]
    public async Task La_scene_choisie_a_l_ecran_arrive_dans_la_ligne_du_journal_des_candidats()
    {
        // Du selecteur de scene jusqu'au disque, par le vrai chemin : scan, jugement, session, journal.
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"drdxt-candidats-scene-{Guid.NewGuid():N}");
        var chemin = System.IO.Path.Combine(dossier, DrDxt.Application.Autopilot.JournalDesCandidats.NomFichier);
        try
        {
            var h = Build(graines: AChaud, pid: PidDuJeu,
                journal: new DrDxt.Application.Autopilot.JournalDesCandidats(chemin, () => "machine-app"));
            await h.Vm.ScanCommand.ExecuteAsync(null);
            h.Vm.SceneDeclaree = h.Vm.ChoixDeScene.Single(c => c.Valeur == SceneRepresentativity.Gameplay);
            h.Vm.DescriptionScene = "  partie reelle, zone calme  ";
            await h.Vm.ApplyCommand.ExecuteAsync(null);

            var ligne = System.Text.Json.JsonSerializer.Deserialize<DrDxt.Application.Autopilot.CandidatJuge>(
                System.IO.File.ReadAllLines(chemin).Single(), new System.Text.Json.JsonSerializerOptions
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                })!;
            ligne.Scene.Should().Be(SceneRepresentativity.Gameplay);
            ligne.Role.Should().Be(SceneRole.Decouverte, "l'App ne declare jamais une validation");
            ligne.DescriptionScene.Should().Be("partie reelle, zone calme", "la precision libre, nettoyee comme a l'ecran");
        }
        finally
        {
            try { System.IO.Directory.Delete(dossier, recursive: true); } catch { /* dossier du test */ }
        }
    }

    [Fact]
    public async Task Des_arrets_brutaux_ne_ferment_ni_le_processeur_ni_le_pilote_et_l_ecran_dit_la_garde()
    {
        // Le processeur se tente sous garde ; 09/10/2026 :
        // le pilote aussi (un arrêt après la pose le rend), et l'écran dit la garde.
        var h = Build(graines: AChaud, pid: PidDuJeu, arretsRecents: 2);
        await ScanThenApply(h);

        h.CandidatsCrees.Should().Contain("fixit.power_plan");
        h.Vm.ResultatOptimisation.Should().StartWith("Ordonnance.Titre").And.Contain("Apply.SousGarde");
    }

    [Fact]
    public async Task Avec_des_arrets_les_corrections_OS_utilisateur_et_HAGS_partent_au_redemarrage()
    {
        var h = Build(graines: new[] { "latency.gameModeOn", "latency.hagsOn", "latency.reflex" },
            pid: PidDuJeu, arretsRecents: 3);
        await ScanThenApply(h);

        await h.Engine.Received(1).ApplyAsync(
            Arg.Is<TweakPlan>(p => p.TweakIds.Order().SequenceEqual(new[] { "latency.gameModeOn", "latency.hagsOn" })),
            Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // HAGS et Reflex (pilote graphique) passent sous garde, comme le reste ; Reflex se tente à chaud.
        h.CandidatsCrees.Should().Equal("latency.reflex");
        h.Vm.ResultatOptimisation.Should().StartWith("Ordonnance.Titre").And.Contain("Apply.SousGarde")
            .And.Contain("Apply.Result.AtReboot · tweak.latency.hagsOn.title").And.NotContain("Apply.Result.Discarded · tweak.latency.hagsOn.title");
    }

    [Fact]
    public async Task Avec_des_arrets_la_correction_du_fichier_d_echange_se_pose_sous_garde()
    {
        // Les arrêts ne bloquent plus ; la correction qui
        // agit au redémarrage se pose (point de restauration d'abord), et l'écran dit la garde.
        var h = Build(graines: new[] { "fixit.power_plan", "fixit.sane_pagefile" }, arretsRecents: 3);
        await ScanThenApply(h);

        await h.Safety.Received().EnsureRestorePointAsync();
        h.Vm.ResultatOptimisation.Should().StartWith("Ordonnance.Titre").And.Contain("Apply.SousGarde");
    }

    // Un levier rendu par la garde d'incident n'est pas reposé avant une semaine, et l'écran le dit ;
    // sinon pose, arrêt, rendu, repose tournaient sans fin.
    [Fact]
    public async Task Un_levier_rendu_par_la_garde_n_est_pas_repose_avant_une_semaine()
    {
        var h = Build(graines: new[] { "fixit.sane_pagefile" });
        var dossier = Directory.CreateTempSubdirectory("drdxt-garde-").FullName;
        try
        {
            h.Vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(Path.Combine(dossier, "changements.json"));
            h.Vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(DateTimeOffset.Now.AddDays(-1),
                "fixit.sane_pagefile", null, ScanViewModel.ClePoseGarde + "fixit.sane_pagefile"));
            await ScanThenApply(h);

            await h.Engine.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default, default, default);
            h.Vm.ResultatOptimisation.Should().Contain("Garde.PasRepose");
            h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingThisRound", "rendu par la garde : pas « déjà en place »");
        }
        finally { Directory.Delete(dossier, true); }
    }

    // HAGS retiré parce qu'il coûtait en jeu (bascule « on » figée « moins bien ») : au scan suivant, HAGS lu éteint le remettait
    // au plan, et Optimiser le reposait sans un mot. Il n'est pas reposé avant trente jours, et l'écran le dit.
    [Fact]
    public async Task Un_levier_retire_parce_qu_il_coutait_n_est_pas_repose_en_silence()
    {
        var h = Build(graines: new[] { "latency.hagsOn" });
        var dossier = Directory.CreateTempSubdirectory("drdxt-retrait-").FullName;
        try
        {
            h.Vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(Path.Combine(dossier, "changements.json"));
            var bascule = new DrDxt.Application.Parties.Changement(DateTimeOffset.Now.AddDays(-3), "HAGS", DateTimeOffset.Now.AddDays(-4), "windows.hags:on");
            h.Vm.JournalChangements.Ajouter(bascule);
            h.Vm.JournalChangements.Figer(bascule, new DrDxt.Application.Parties.BilanDuChangement(
                DrDxt.Application.Parties.VerdictEnJeu.Pire, "cod.exe", 3, 3, 95, 81, 140, 140));

            await ScanThenApply(h);

            await h.Engine.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default, default, default);
            h.Vm.ResultatOptimisation.Should().Contain("Retrait.PasRepose");
        }
        finally { Directory.Delete(dossier, true); }
    }

    // « Déjà en place » ne se dit que quand aucun réglage n'était candidat ; un réglage écarté (sans jeu, veto, garde) se dit
    // « rien à ce clic », jamais « déjà en place ».
    [Fact]
    public async Task Sans_aucun_candidat_rien_a_poser_se_dit_deja_en_place()
    {
        var h = Build(graines: [], pid: PidDuJeu);
        await ScanThenApply(h);

        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingApplicable");
    }

    [Fact]
    public async Task Sans_arrets_rien_a_tenter_reste_rien_a_appliquer()
    {
        var h = Build(graines: AChaud, pid: 0);
        await ScanThenApply(h);

        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingThisRound");
        h.Vm.ResultatOptimisation.Should().NotContain("Apply.SousGarde");
    }

    [Fact]
    public async Task Sans_jeu_un_levier_a_chaud_n_est_pas_a_tenter_et_rien_n_est_vendu()
    {
        // Le client payait et creait un point de restauration pour lire
        // ensuite « lance ton jeu ». Le jeu entre dans le JUGEMENT, donc avant l'achat.
        var h = Build(graines: AChaud, pid: 0);
        await ScanThenApply(h);

        h.CandidatsCrees.Should().BeEmpty();
        await h.Safety.DidNotReceive().EnsureRestorePointAsync();
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingThisRound");
        h.Vm.ResultatOptimisation.Should().Contain("aucun jeu");
    }
}
