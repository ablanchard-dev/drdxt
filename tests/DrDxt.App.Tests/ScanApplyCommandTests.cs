using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Navigation;
using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Profiles;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// L8 b — « OPTIMISER » (revue du 10/09/2026).
//
//   * juger avant d'encaisser : rien a tenter ⇒ motifs, et jamais l'achat ;
//   * gate paye, puis point de restauration (§16), avant toute ecriture ;
//   * ce qui ne se mesure pas (effet au redemarrage) part au moteur sans baseline ;
//   * Un seul levier a chaud part en session mesuree ; un levier ecarte n'est jamais applique.
//
// Every seam is a fake — NO real elevation, NO real registry, NO real System Restore.
// -----------------------------------------------------------------------
public sealed class ScanApplyCommandTests
{
    private const int Score = 68;
    private const int PidDuJeu = 4242;

    private static readonly string[] AuRedemarrage = { "latency.hagsOn", "latency.gameModeOn" };
    private static readonly string[] AChaud = { "fixit.power_plan", "latency.reflex" };

    // ---- shared fakes -------------------------------------------------

    private static HardwareInventory Hw(ChassisType c = ChassisType.Desktop) => new(
        new CpuInfo("AMD Ryzen 7 7800X3D", "AMD", 8, 16, true, 1),
        new[] { new GpuInfo("NVIDIA RTX 4070", "NVIDIA", 12L << 30, "551", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        c);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static BenchmarkResult Bl => new(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

    private static ApplyReport OkReport(params string[] appliedIds) =>
        new(appliedIds.Select(id => new ApplyOutcome(id, Applied: true, AutoReverted: false,
                new TweakSnapshot(id, "{}"), Error: null)).ToList(),
            Verdict: null);

    /// <summary>Un levier a chaud de laboratoire : il s'applique, se relit, se rend.</summary>
    private sealed class CandidatFaux(CandidateDescriptor d) : IAutopilotCandidate
    {
        public CandidateDescriptor Descriptor => d;
        public Result Apply() => Result.Ok();
        public Result Revert() => Result.Ok();
        public Result<bool> ReadBack() => Result<bool>.Ok(true);
    }

    private sealed class Harness
    {
        public required ScanViewModel Vm { get; init; }
        public DrDxt.App.Platform.IGameLocator? Locator { get; init; }
        public required ISafetyManager Safety { get; init; }
        public required ITweakEngine Engine { get; init; }
        public required INavigationService Nav { get; init; }
        public required List<string> CandidatsCrees { get; init; }
        public required IGpuTuningApplyStep GpuStep { get; init; }
    }

    private static Harness Build(
        string[]? graines = null,
        ScanProfileConsumption? consumption = null,
        Result? restoreResult = null,
        Result<ApplyReport>? applyResult = null,
        int pid = 0,
        int arretsRecents = 0,
        DrDxt.Application.Autopilot.JournalDesCandidats? journal = null,
        string? nomDuJeu = null)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(Bl));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St,
              new List<Finding> { new("Memory", Severity.Major, "Finding.Memory.XmpOff", 12) },
              Score, Bl,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        var safety = Substitute.For<ISafetyManager>();
        safety.EnsureRestorePointAsync().Returns(restoreResult ?? Result.Ok());

        var engine = Substitute.For<ITweakEngine>();
        engine.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(applyResult ?? Result<ApplyReport>.Ok(OkReport(AuRedemarrage)));

        var consumer = ScanViewModelTestSeams.ProfileConsumer(
            consumption ?? ScanViewModelTestSeams.PlanConsumption(graines ?? AuRedemarrage));

        var nav = Substitute.For<INavigationService>();

        var locator = Substitute.For<DrDxt.App.Platform.IGameLocator>();
        locator.LocateRunningGamePid().Returns(pid > 0 ? pid : null);
        if (nomDuJeu is not null) locator.NameOfGame(pid).Returns(nomDuJeu);

        var crees = new List<string>();
        IReadOnlyList<ArretClasse> arrets = Enumerable.Range(1, arretsRecents)
            .Select(j => ArretClasse.SansDetail(DateTimeOffset.UtcNow.AddDays(-j))).ToList();

        var gpuStep = ScanViewModelTestSeams.GpuTuningStep();
        var vm = new ScanViewModel(
            hd, sr, br, sc, sd, safety, engine, consumer, gpuStep,
            nav, () => new DashboardViewModel(), ScanViewModelTestSeams.DesiredStateStore(),
            new AppliedTweakManifestMapper(), ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key,
            gameLocator: locator,
            creerCandidat: d => { crees.Add(d.Id); return new CandidatFaux(d); },
            lireStabilite: () => (null, arrets),
            journalCandidats: journal);

        return new Harness { Vm = vm, Safety = safety, Engine = engine, Nav = nav, CandidatsCrees = crees, GpuStep = gpuStep, Locator = locator };
    }

    private static async Task ScanThenApply(Harness h)
    {
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyCommand.ExecuteAsync(null);
    }

    private static Task EngineReceived(Harness h, int fois) =>
        h.Engine.Received(fois).ApplyAsync(
            Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

    // ====================== enablement ======================

    [Fact]
    public void Apply_is_disabled_before_a_scan_has_results()
        => Build().Vm.ApplyCommand.CanExecute(null).Should().BeFalse();

    [Fact]
    public async Task Apply_becomes_enabled_after_a_successful_scan()
    {
        var h = Build();
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Vm.ApplyCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Optimiser_est_offert_a_tous_c_est_lui_qui_juge()
    {
        // 23/09/2026 — DrDXT est gratuit : aucune licence ne conditionne l'action principale.
        var h = Build();
        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.CanApply.Should().BeTrue();
    }

    // ====================== le cerveau : juger, puis un levier ======================

    [Fact]
    public async Task Les_corrections_au_redemarrage_vont_au_moteur_SANS_baseline_et_sans_levier_a_chaud()
    {
        var h = Build(graines: new[] { "latency.hagsOn", "latency.gameModeOn", "fixit.power_plan" });
        await ScanThenApply(h);

        await h.Engine.Received(1).ApplyAsync(
            Arg.Is<TweakPlan>(p => p.TweakIds.OrderBy(x => x).SequenceEqual(new[] { "latency.gameModeOn", "latency.hagsOn" })),
            Arg.Is<BenchmarkResult?>(b => b == null),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.RebootSet");
    }

    // 07/10/2026 (L-ECRAN) : le moteur défait un lot entier sur un échec. Un mode d'écran que le joueur ne garde pas ne
    // doit pas défaire les heures d'activité ni les corrections au redémarrage cliquées avec lui : chacun son lot.
    [Fact]
    public async Task Un_mode_d_ecran_refuse_ne_defait_pas_les_autres_reglages_au_clic()
    {
        var h = Build(graines: new[] { "latency.hagsOn", "fixit.heures_actives", "fixit.ecran_frequence" });
        h.Engine.ApplyAsync(Arg.Is<TweakPlan>(p => p.TweakIds.Contains("fixit.ecran_frequence")), Arg.Any<BenchmarkResult?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<ApplyReport>.Fail(ErrorCodes.TweakSdkUnavailable, "le joueur ne garde pas le mode"));
        await ScanThenApply(h);

        await h.Engine.Received(1).ApplyAsync(Arg.Is<TweakPlan>(p => p.TweakIds.SequenceEqual(new[] { "fixit.heures_actives" })),
            Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await h.Engine.Received(1).ApplyAsync(Arg.Is<TweakPlan>(p => p.TweakIds.SequenceEqual(new[] { "latency.hagsOn" })),
            Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        h.Vm.ApplyBlockedMessage.Should().BeNullOrEmpty();
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.NotHeld", "F-16 : le lot refusé se nomme, il ne disparaît pas");
    }

    [Fact]
    public async Task Quand_Windows_garde_un_point_existant_l_ecran_dit_lequel_protege()
    {
        // 14/09/2026 : un 2e « Optimiser » dans la journee n'est plus bloque ; le point que Windows a
        // garde protege le changement, et le client doit savoir de quand il date.
        var h = Build(graines: new[] { "latency.hagsOn", "latency.gameModeOn", "fixit.power_plan" });
        h.Safety.PointReutiliseUtc.Returns(new DateTime(2026, 9, 14, 7, 0, 0, DateTimeKind.Utc));

        await ScanThenApply(h);

        h.Vm.ResultatOptimisation.Should().Contain("Apply.RestorePoint.Reused");
    }

    [Fact]
    public async Task Un_SEUL_levier_a_chaud_part_en_session()
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        await ScanThenApply(h);

        h.CandidatsCrees.Should().Equal(new[] { "fixit.power_plan" }, "un seul KeepPolicy : jamais deux leviers");
        await EngineReceived(h, 0);
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Result.ToMeasure · tweak.fixit.power_plan.title")
            .And.Contain("Apply.Result.NotThisRound · tweak.latency.reflex.title")
            .And.Contain("personne n'a dit ce qui était joué", "aucune scene n'est fournie dans ce test : le contrat doit le dire");
    }

    // 26/09/2026 : sur un seul écran, le clic sur « Optimiser » réduit un plein écran exclusif. La session
    // attend que le jeu revienne à l'écran ; s'il reste réduit, elle n'a pas lieu et c'est dit.
    [Fact]
    public async Task Jeu_reduit_au_clic_la_session_attend_son_retour_puis_a_lieu()
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Locator!.LocateRunningGamePid().Returns((int?)null, (int?)null, PidDuJeu);
        h.Locator.JeuOuvert().Returns(true);
        var attentes = new List<bool>();
        h.Vm.Patienter = _ => { attentes.Add(h.Vm.AttenteDuJeuSession && !h.Vm.AttenteDuJeu); return Task.CompletedTask; };

        await h.Vm.ApplyCommand.ExecuteAsync(null);

        // Deux pas d'attente (le jeu revient au second), puis ceux de la surveillance de la session.
        attentes.Should().HaveCountGreaterThanOrEqualTo(2);
        attentes.Take(2).Should().OnlyContain(a => a);
        h.CandidatsCrees.Should().Equal(new[] { "fixit.power_plan" });
        h.Vm.ResultatOptimisation.Should().NotContain("Apply.Session.JeuReduit");
    }

    // Le joueur revient sur DrDXT en pleine session : le jeu est réduit, l'A/B mêle partie et jeu réduit.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quitter_le_jeu_pendant_la_session_est_dit_avec_le_verdict(bool quitte)
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        await h.Vm.ScanCommand.ExecuteAsync(null);
        if (quitte) h.Locator!.LocateRunningGamePid().Returns(PidDuJeu, PidDuJeu, (int?)null);
        h.Vm.Patienter = _ => Task.CompletedTask;

        await h.Vm.ApplyCommand.ExecuteAsync(null);

        h.CandidatsCrees.Should().Equal(new[] { "fixit.power_plan" }, "temoin : la session a bien eu lieu");
        if (quitte) h.Vm.ResultatOptimisation.Should().Contain("Apply.Session.JeuQuitte");
        else h.Vm.ResultatOptimisation.Should().NotContain("Apply.Session.JeuQuitte");
    }

    [Fact]
    public async Task Jeu_reste_reduit_la_session_n_a_pas_lieu_et_c_est_dit()
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Locator!.LocateRunningGamePid().Returns((int?)null);
        h.Locator.JeuOuvert().Returns(true);
        h.Vm.Patienter = _ => Task.CompletedTask;

        await h.Vm.ApplyCommand.ExecuteAsync(null);

        h.CandidatsCrees.Should().BeEmpty("aucun levier n'est posé sur un jeu qu'on ne peut pas mesurer");
        h.Vm.ResultatOptimisation.Should().Contain("Apply.Session.JeuReduit");
    }

    // ====================== la scene declaree (11/09/2026) ======================
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

    // 29/09/2026, seconde critique : Optimiser ne disait pas, avant le clic, ce qu'il allait changer. Après le
    // scan, l'écran nomme les réglages qu'il essaiera.
    [Fact]
    public async Task Avant_le_clic_Optimiser_nomme_ce_qu_il_essaiera()
    {
        var h = Build(graines: AChaud, pid: PidDuJeu);
        h.Vm.ApercuOptimisation.Should().BeNull("rien n'est prévu avant le scan");

        await h.Vm.ScanCommand.ExecuteAsync(null);

        foreach (var id in AChaud)
            h.Vm.ApercuOptimisation.Should().Contain(TweakDisplayKeys.TitleKey(id));
    }

    // UI1 (critique du 08/10, défaut 1) : « Rien à changer automatiquement ici » s'affichait juste au-dessus de « DrDXT
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

    // 29/09/2026, troisième critique : l'aperçu promettait le VRR sans dire qu'il n'est pas encore validé sur une
    // vraie machine, alors que la carte Latence le dit. L'aperçu porte la même réserve.
    [Fact]
    public void L_apercu_dit_quand_un_reglage_n_est_pas_encore_valide()
    {
        static DrDxt.App.Profiles.ProfileRecommendationItem Item(string id, bool teste) =>
            new(id, default, "c", "s", default, default, teste, true, null);

        var apercu = ScanViewModel.ApercuDe(["a", "b"], [Item("a", true), Item("b", false)], id => "T-" + id, "Essaiera :", "Sans validation :", "Aucune validation.");

        // 29/09/2026, photo de l'accueil : « Optimisations pour les jeux en fenêtre (VRR) pas encore validé », la
        // réserve collée au titre s'accordait mal. Elle devient une phrase à part, sans participe à accorder.
        apercu.Should().Be("Essaiera : T-a, T-b. Sans validation : T-b.");
        ScanViewModel.ApercuDe(["a"], [Item("a", true)], id => "T-" + id, "Essaiera :", "Sans validation :", "Aucune validation.")
            .Should().Be("Essaiera : T-a", "témoin : tout validé, pas de réserve");

        // Un seul réglage, sans validation : le répéter derrière la réserve le disait deux fois de suite.
        ScanViewModel.ApercuDe(["b"], [Item("b", false)], id => "T-" + id, "Essaiera :", "Sans validation :", "Aucune validation.")
            .Should().Be("Essaiera : T-b. Aucune validation.");
    }

    // 29/09/2026, critique de l'app : le sélecteur de scène revenait en entier avant Optimiser, déjà répondu
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

    // 29/09/2026, photo de l'accueil : « Pendant la mesure en jeu, tu joues : Je ne sais pas ». Dans le
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

        // 23/09/2026 — cette liste etait ecrite a la main, dans le produit comme dans ce test : les
        // deux disaient la meme chose, donc le test ne pouvait pas voir une scene manquante. Elle se
        // DEDUIT maintenant de l'enum ; ajouter un membre sans lui donner de case a l'ecran rougit ici.
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
    // 23/09/2026 — « je n'etais pas en jeu » doit arriver a l'ecran avec sa phrase : la machine
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
        // 08/10/2026 (le mainteneur : « DrDXT n'est pas censé me dire non ») : le processeur se tente sous garde ; 09/10/2026 :
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
        // 09/10/2026 : HAGS et Reflex (pilote graphique) passent sous garde, comme le reste ; Reflex se tente à chaud.
        h.CandidatsCrees.Should().Equal("latency.reflex");
        h.Vm.ResultatOptimisation.Should().StartWith("Ordonnance.Titre").And.Contain("Apply.SousGarde")
            .And.Contain("Apply.Result.AtReboot · tweak.latency.hagsOn.title").And.NotContain("Apply.Result.Discarded · tweak.latency.hagsOn.title");
    }

    [Fact]
    public async Task Avec_des_arrets_la_correction_du_fichier_d_echange_se_pose_sous_garde()
    {
        // 08/10/2026 (le mainteneur : « DrDXT n'est pas censé me dire non ») : les arrêts ne bloquent plus ; la correction qui
        // agit au redémarrage se pose (point de restauration d'abord), et l'écran dit la garde.
        var h = Build(graines: new[] { "fixit.power_plan", "fixit.sane_pagefile" }, arretsRecents: 3);
        await ScanThenApply(h);

        await h.Safety.Received().EnsureRestorePointAsync();
        h.Vm.ResultatOptimisation.Should().StartWith("Ordonnance.Titre").And.Contain("Apply.SousGarde");
    }

    // GARDE1 (09/10/2026) : un levier rendu par la garde d'incident n'est pas reposé avant une semaine, et l'écran le dit ;
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
        }
        finally { Directory.Delete(dossier, true); }
    }

    [Fact]
    public async Task Sans_arrets_rien_a_tenter_reste_rien_a_appliquer()
    {
        var h = Build(graines: AChaud, pid: 0);
        await ScanThenApply(h);

        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingApplicable");
        h.Vm.ResultatOptimisation.Should().NotContain("Apply.SousGarde");
    }

    [Fact]
    public async Task Sans_jeu_un_levier_a_chaud_n_est_pas_a_tenter_et_rien_n_est_vendu()
    {
        // Revue du 10/09 : le client payait et creait un point de restauration pour lire
        // ensuite « lance ton jeu ». Le jeu entre dans le JUGEMENT, donc avant l'achat.
        var h = Build(graines: AChaud, pid: 0);
        await ScanThenApply(h);

        h.CandidatsCrees.Should().BeEmpty();
        await h.Safety.DidNotReceive().EnsureRestorePointAsync();
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingApplicable");
        h.Vm.ResultatOptimisation.Should().Contain("aucun jeu");
    }

    // ====================== le tuning GPU passe par le meme jugement (revue du 12/09, C3) ======================
    // Le bouton GPU ecrivait sur la carte sans jamais demander au selecteur, alors que le diagnostic
    // porte la decision « rien ne s'ecrit sur la carte depuis l'App » et le veto des arrets brutaux.

    private static ScanProfileConsumption AvecPlanGpu() =>
        ScanViewModelTestSeams.PlanConsumption(System.Array.Empty<string>())
            with { GpuPlan = new TweakPlan(new[] { "gpu.undervolt", "gpu.power_limit" }) };

    private static async Task ScanThenApplyGpu(Harness h)
    {
        await h.Vm.ScanCommand.ExecuteAsync(null);
        await h.Vm.ApplyGpuTuningCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Sans_arret_qui_touche_la_carte_le_tuning_GPU_se_mesure_en_jeu_et_jamais_par_lot()
    {
        // 06/10/2026 (L-GPU) : le drapeau global retiré, la carte se règle comme tout levier à chaud : point de
        // restauration, puis la session mesurée en jeu (gardé seulement avec un gain mesuré). L'étape par lot, qui
        // gardait un réglage sans gain, n'est plus jamais appelée.
        var h = Build(consumption: AvecPlanGpu(), pid: PidDuJeu);
        await ScanThenApplyGpu(h);

        await h.GpuStep.DidNotReceive().ApplyAsync(Arg.Any<ScanProfileConsumption>(),
            Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await h.Safety.Received(1).EnsureRestorePointAsync();
        h.CandidatsCrees.Should().Equal("gpu.power_limit"); // 07/10 : la fréquence reste fermée (règle du 29/09)
        h.Vm.PhraseReglageCarte.Should().StartWith("Réglage de la carte ouvert chez toi");
    }

    [Fact]
    public async Task Optimiser_atteint_la_carte_le_bouton_visible_et_pas_seulement_la_commande_sans_bouton()
    {
        // 06/10/2026 (L-GPU) : la commande GPU dédiée n'a aucun bouton ; rouvrir la carte n'a de sens que si
        // « Optimiser » la propose, mesurée en jeu comme le reste.
        var h = Build(consumption: AvecPlanGpu(), pid: PidDuJeu);
        await ScanThenApply(h);

        h.CandidatsCrees.Should().Equal("gpu.power_limit");
        h.Vm.ApercuOptimisation.Should().Contain("tweak.gpu.power_limit.title").And.NotContain("tweak.gpu.undervolt.title");
    }

    [Fact]
    public async Task Des_arrets_secs_repetes_ouvrent_la_carte_sous_garde_et_la_phrase_le_dit()
    {
        var h = Build(consumption: AvecPlanGpu() with
        {
            Items = [ScanViewModelTestSeams.Item("gpu.undervolt", inPlan: false, intent: ProfileIntent.GpuTune, applicable: true)],
        }, pid: PidDuJeu, arretsRecents: 2);
        await ScanThenApplyGpu(h);

        // 09/10/2026 (le mainteneur : « DrDXT n'est pas censé me dire non ») : la limite de puissance se tente sous garde ;
        // la fréquence et la tension restent hors d'Optimiser (règle du 29/09), quels que soient les arrêts.
        h.CandidatsCrees.Should().Equal("gpu.power_limit");
        h.Vm.PhraseReglageCarte.Should().StartWith("Réglage de la carte ouvert sous garde").And.Contain("arrêt sec sans code")
            .And.NotContain("inattribuable");
        h.Vm.ApercuOptimisation.Should().NotContain("tweak.gpu.undervolt.title");
    }

    [Fact]
    public async Task Avec_des_arrets_secs_repetes_le_tuning_GPU_se_tente_sous_garde()
    {
        var h = Build(consumption: AvecPlanGpu(), pid: PidDuJeu, arretsRecents: 3);
        await ScanThenApplyGpu(h);

        // 09/10/2026 : comme sans arrêt (la session mesurée en jeu, jamais l'étape par lot), et la phrase dit la garde.
        await h.GpuStep.DidNotReceive().ApplyAsync(Arg.Any<ScanProfileConsumption>(),
            Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        h.CandidatsCrees.Should().Equal("gpu.power_limit");
        h.Vm.PhraseReglageCarte.Should().StartWith("Réglage de la carte ouvert sous garde");
    }

    [Fact]
    public async Task Sans_jeu_le_tuning_GPU_n_est_pas_tente()
    {
        var h = Build(consumption: AvecPlanGpu(), pid: 0);
        await ScanThenApplyGpu(h);

        await h.GpuStep.DidNotReceive().ApplyAsync(Arg.Any<ScanProfileConsumption>(),
            Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.NothingApplicable");
    }

    // ====================== restore-point gate (§16) ======================

    [Fact]
    public async Task Restore_point_gate_runs_before_any_apply()
    {
        var h = Build();
        await ScanThenApply(h);

        Received.InOrder(() =>
        {
            h.Safety.EnsureRestorePointAsync();
            h.Engine.ApplyAsync(Arg.Any<TweakPlan>(), Arg.Any<BenchmarkResult?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Restore_disabled_blocks_apply_and_shows_localized_message()
    {
        var h = Build(graines: new[] { "latency.hagsOn", "fixit.power_plan" }, pid: PidDuJeu,
            restoreResult: Result.Fail(ErrorCodes.RestoreDisabled, "off"));
        await ScanThenApply(h);

        await EngineReceived(h, 0);
        h.CandidatsCrees.Should().BeEmpty("rien ne s'ecrit sans point de restauration");
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.RestoreRequired.Blocked");
        h.Nav.DidNotReceive().NavigateToDashboard(Arg.Any<object>());
    }

    [Fact]
    public async Task Restore_declined_blocks_apply_with_message()
    {
        var h = Build(restoreResult: Result.Fail(ErrorCodes.RestoreDeclined, "declined"));
        await ScanThenApply(h);

        await EngineReceived(h, 0);
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.RestoreRequired.Declined");
    }

    // 06/10/2026 (plan 4.5) : chez moi, Windows n'a pas réussi à créer le point et l'écran disait « la restauration
    // est désactivée » alors qu'elle était active. Chaque cause a ses mots, et la raison de Windows est dite.
    [Fact]
    public async Task Un_point_que_Windows_n_a_pas_cree_ne_se_dit_pas_restauration_desactivee()
    {
        var h = Build(restoreResult: Result.Fail(ErrorCodes.RestoreFailed, "aucun point neuf"));
        await ScanThenApply(h);

        await EngineReceived(h, 0);
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.RestoreRequired.Failed", "pas « Blocked », qui dit la restauration désactivée");
    }

    [Fact]
    public async Task Un_point_accepte_mais_introuvable_le_dit_sans_accuser_Windows()
    {
        var h = Build(restoreResult: Result.Fail(ErrorCodes.RestoreNotVerified, "rien en 21 s"));
        await ScanThenApply(h);

        await EngineReceived(h, 0);
        h.Vm.ApplyBlockedMessage.Should().Be("Apply.RestoreRequired.NotFound");
    }

    // 06/10/2026, chez moi : « je clique et rien ». « Optimiser » avait posé le VRR, mais son résultat (et ses refus)
    // s'écrivaient ~900 lignes plus bas, sous le pli. Le résultat suit le bouton, avant le choix de scène.
    [Fact]
    public void Le_resultat_d_Optimiser_s_affiche_sous_le_bouton()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var bouton = xaml.IndexOf("x:Name=\"BoutonOptimiser\"", StringComparison.Ordinal);
        var scene = xaml.IndexOf("{Binding LabelSceneRetenue}", StringComparison.Ordinal);
        bouton.Should().BePositive("témoin");
        scene.Should().BeGreaterThan(bouton, "témoin");
        foreach (var message in new[] { "ResultatOptimisation", "ApplyBlockedMessage", "MoatUnarmedMessage", "ManifesteIllisibleMessage" })
            xaml.IndexOf($"{{Binding {message}}}", StringComparison.Ordinal).Should()
                .BeInRange(bouton, scene, $"{message} doit se lire sous le bouton qui le produit");
    }

    // Revue du 06/10 (P2) : jusqu'à ~40 s sans retour (création du point puis relectures) ; le bouton le dit.
    [Fact]
    public void Optimiser_dit_qu_il_travaille_pendant_le_point_de_restauration()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var apres = xaml[xaml.IndexOf("x:Name=\"BoutonOptimiser\"", StringComparison.Ordinal)..];
        apres.Should().Contain("{Binding LabelOptimisationEnCours}");
        apres[..apres.IndexOf("LabelOptimisationEnCours", StringComparison.Ordinal)]
            .Should().Contain("ApplyCommand.IsRunning", "le texte n'apparaît que pendant le travail");
    }

    [Fact]
    public async Task Engine_failure_surfaces_error()
    {
        var h = Build(applyResult: Result<ApplyReport>.Fail(ErrorCodes.BatchReverted, "boom"));
        await ScanThenApply(h);

        h.Vm.ApplyBlockedMessage.Should().Be("Apply.Failed");
        h.Nav.DidNotReceive().NavigateToDashboard(Arg.Any<object>());
    }

    // ====================== scan surfaces the profile recommendations (§16) ======================

    [Fact]
    public async Task Scan_surfaces_profile_recommendations_with_evidence_and_unverified_marking()
    {
        var consumption = ScanViewModelTestSeams.PlanConsumption(
            new[] { "fixit.power_plan" },
            ScanViewModelTestSeams.Item("fixit.power_plan", tested: false, inPlan: true,
                claim: "cooler, steadier", source: "CAHIER §4"),
            ScanViewModelTestSeams.Item("latency.render_queue_cap", tested: false, inPlan: true,
                intent: ProfileIntent.Latency));
        var h = Build(consumption: consumption);

        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.Recommendations.Should().HaveCount(2);
        h.Vm.Recommendations.Should().Contain(i =>
            i.TweakId == "fixit.power_plan" && i.Claim == "cooler, steadier" && i.Source == "CAHIER §4");
        h.Vm.Recommendations.Should().OnlyContain(i => !i.IsVerified);
        h.Vm.LabelUnverified.Should().Be("UI.Profile.Unverified");

        // 27/09/2026, vu sur photo (latence) : l'étiquette « non vérifié » ne disait pas CE qui ne l'était
        // pas, et la même chaîne veut dire « état illisible » côté BIOS. Sur la carte, un réglage proposé
        // non encore validé le dit en clair.
        h.Vm.Domaines.SelectMany(d => d.Proposes).Should().NotBeEmpty()
            .And.OnlyContain(p => p.Valeur == "Carte.Propose.NonValide");

        // 29/09/2026, photo (latence) : le titre en gras était tout l'argumentaire, trois lignes. Le titre est le
        // nom du réglage, celui qu'Optimiser annonce ; l'argumentaire passe dessous.
        var propose = h.Vm.Domaines.SelectMany(d => d.Proposes).Single(p => p.Detail.Contains("cooler, steadier"));
        propose.Titre.Should().Be(TweakDisplayKeys.TitleKey("fixit.power_plan"));
    }

    [Fact]
    public async Task Ce_qui_ne_s_applique_pas_ou_ne_sera_jamais_ecrit_n_est_pas_recommande()
    {
        // 16/09/2026, scan reel de la machine de reference : 10 « optimisations recommandees »
        // affichees, dont 5 que le catalogue declare NON applicables a cette machine et 2 reglages
        // GPU que la decision de fermeture n'ecrira jamais. L'ecran les recommandait quand meme.
        var consumption = ScanViewModelTestSeams.PlanConsumption(
            new[] { "latency.gameDvrBackgroundOff" },
            ScanViewModelTestSeams.Item("latency.gameDvrBackgroundOff", inPlan: true, intent: ProfileIntent.Latency),
            ScanViewModelTestSeams.Item("latency.hagsOn", inPlan: false, intent: ProfileIntent.Latency, applicable: false),
            ScanViewModelTestSeams.Item("gpu.undervolt", inPlan: false, intent: ProfileIntent.GpuTune, applicable: true));
        var h = Build(consumption: consumption);

        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.Recommendations.Select(i => i.TweakId).Should().NotContain("latency.hagsOn",
            "un levier que le catalogue declare inapplicable n'est pas une recommandation");
        // 06/10/2026 (L-GPU) : la carte est ouverte machine par machine ; sans arret qui la touche, elle se recommande.
        h.Vm.Recommendations.Select(i => i.TweakId).Should().Contain("gpu.undervolt");
        h.Vm.Recommendations.Select(i => i.TweakId).Should().Contain("latency.gameDvrBackgroundOff");
    }

    [Fact]
    public async Task Sans_recommandation_l_ecran_le_dit_en_clair()
    {
        // 16/09/2026 : sur la machine de reference, plus aucun levier logiciel n'est recommande. L'ecran
        // affichait « Optimisations recommandees 0 » et une liste vide, sans une phrase.
        var h = Build(consumption: ScanViewModelTestSeams.EmptyConsumption());
        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.AucuneRecommandation.Should().BeTrue();
        h.Vm.LabelAucuneRecommandation.Should().Be("Scan.Recommendations.None");
    }

    [Fact]
    public async Task Empty_profile_plan_blocks_with_nothing_to_apply_message()
    {
        var h = Build(consumption: ScanViewModelTestSeams.EmptyConsumption());
        await ScanThenApply(h);

        await h.Safety.DidNotReceive().EnsureRestorePointAsync();
        await EngineReceived(h, 0);
        h.Vm.ApplyBlockedMessage.Should().NotBeNullOrEmpty();
    }
}
