using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Measurement;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Le scan doit laisser une trace, sinon la derive est indetectable.
///
/// Un seul fichier survivait a une session de DrDXT : `memory-baseline.json`. Le produit
/// redecouvrait la machine a chaque lancement et ne pouvait donc pas repondre a la question que le
/// joueur pose vraiment — **« mon PC est-il plus lent qu'avant ? »**. Une degradation lente est
/// precisement ce qu'aucun scan isole ne voit : chaque mesure prise seule parait normale.
///
/// Ces tests ne verifient pas qu'un fichier existe. Ils verifient que le SCAN ecrit, que ce qu'il
/// ecrit permet de conclure au scan suivant, et qu'un journal casse n'empeche pas de mesurer
/// aujourd'hui.
/// </summary>
public sealed class ScanJournalsThePerformanceTests
{
    private sealed class FichierFake : IPerformanceJournalFile
    {
        public string? Contenu;
        public bool EchecEcriture;
        public Result WriteAtomic(string json)
        {
            if (EchecEcriture) return Result.Fail(ErrorCodes.CaptureFail, "disque plein");
            Contenu = json;
            return Result.Ok();
        }
        public Result<string?> Read() => Result<string?>.Ok(Contenu);
    }

    /// <summary>Un jeu qui tourne et qui a un NOM. Sans nom, le journal ne compare rien : « pas
    /// identifie » n'est pas « le meme jeu ».</summary>
    private sealed class JeuFictif(string? nom, int pid = 4242) : DrDxt.App.Platform.IGameLocator
    {
        public int? LocateRunningGamePid() => pid;
        public string? NameOfGame(int _) => nom;
    }

    private static HardwareInventory Hw() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
        new[] { new GpuInfo("NVIDIA RTX 4060", "NVIDIA", 8L << 30, "551", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3800, 4000), new MemoryModule("PN", "Kingston", 3800, 4000) },
        new MotherboardInfo("Gigabyte", "B760"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static ScanViewModel Vm(PerformanceJournalStore? journal, double avgFps, double marge = 2.0,
        string machine = "PC-A", string? jeu = "warzone", Func<string, string, string>? writeReport = null)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var bl = new BenchmarkResult(avgFps, avgFps * 0.75, avgFps * 0.6,
            new List<double> { 1000.0 / avgFps }, null, marge, 3, HygieneKeptFraction: 1.0);

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(bl));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St, new List<Finding>(), 68, bl, new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        var store = Substitute.For<IDesiredStateStore>();
        store.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Ok());
        store.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));

        return new ScanViewModel(
            hd, sr, br, sc, sd,
            ScanViewModelTestSeams.Safety(), ScanViewModelTestSeams.Engine(),
            ScanViewModelTestSeams.ProfileConsumer(), ScanViewModelTestSeams.GpuTuningStep(),
            ScanViewModelTestSeams.Navigation(), ScanViewModelTestSeams.DashboardFactory(),
            store, new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key,
            gameLocator: new JeuFictif(jeu),
            machineId: () => machine,
            performanceJournal: journal,
            writeReport: writeReport);
    }

    [Fact]
    public async Task Le_scan_ECRIT_sa_mesure_dans_le_journal()
    {
        var f = new FichierFake();
        var journal = new PerformanceJournalStore(f);

        await Vm(journal, avgFps: 200).ScanCommand.ExecuteAsync(null);

        var relu = journal.Lire();
        relu.Should().ContainSingle("un scan qui ne laisse aucune trace ne peut rien comparer demain");
        relu[0].AvgFps.Should().Be(200);
        relu[0].MachineId.Should().Be("PC-A");
    }

    [Fact]
    public async Task Deux_scans_espaces_permettent_de_CONCLURE_a_une_derive()
    {
        // Toute la raison d'etre du journal, vue du produit et pas de la fonction pure.
        var f = new FichierFake();

        await Vm(new PerformanceJournalStore(f), avgFps: 200).ScanCommand.ExecuteAsync(null);
        await Vm(new PerformanceJournalStore(f), avgFps: 160).ScanCommand.ExecuteAsync(null);

        var derive = PerformanceJournal.Derive(new PerformanceJournalStore(f).Lire());
        derive.RegressionEnJeuProuvee.Should().BeTrue("-20 % depasse largement le bruit de 2 fps");
    }

    // ── Et le produit le dit, au lieu de laisser un test faire le calcul ────────────────────
    //
    // Le test juste au-dessus appelait `PerformanceJournal.Derive` LUI-meme : il prouvait que
    // la donnee permettait de conclure, pendant que le produit ecrivait un journal que personne
    // ne relisait. Le juge n'etait appele que par des tests. Ceux qui suivent portent sur la
    // ligne que le client lit.

    // 27/09/2026 : le rapport envoyé au support porte la même phrase que l'écran.
    [Fact]
    public async Task Le_rapport_dit_aussi_que_la_machine_a_ralenti()
    {
        var f = new FichierFake();
        await Vm(new PerformanceJournalStore(f), avgFps: 200).ScanCommand.ExecuteAsync(null);
        string? ecrit = null;
        var vm = Vm(new PerformanceJournalStore(f), avgFps: 160, writeReport: (_, texte) => { ecrit = texte; return "r.txt"; });
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.ExportReportCommand.ExecuteAsync(null);

        ecrit.Should().Contain("--- Dérive des performances ---").And.Contain("Scan.Drift.Regression");
    }

    [Fact]
    public async Task Le_scan_DIT_a_l_ecran_que_la_machine_a_ralenti()
    {
        var f = new FichierFake();

        await Vm(new PerformanceJournalStore(f), avgFps: 200).ScanCommand.ExecuteAsync(null);
        var vm = Vm(new PerformanceJournalStore(f), avgFps: 160);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.PerformanceDriftLabel.Should().Be("Scan.Drift.Regression",
            "la localisation factice rend la cle : c'est la PHRASE CHOISIE qui se verifie ici, "
            + "le texte lui-meme etant verrouille par ScanLocalizationTests");
    }

    [Fact]
    public async Task Un_ecart_SOUS_la_resolution_se_dit_comme_une_borne_et_pas_comme_une_alerte()
    {
        // Le cas le plus frequent, et celui qui decide si la ligne reste lisible : 200 -> 199
        // avec deux marges de 2 fps. Un produit qui crie a la degradation tous les mois n'est
        // plus lu — et « pas resolu » n'est pas « rien n'a bouge ».
        var f = new FichierFake();

        await Vm(new PerformanceJournalStore(f), avgFps: 200).ScanCommand.ExecuteAsync(null);
        var vm = Vm(new PerformanceJournalStore(f), avgFps: 199);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.PerformanceDriftLabel.Should().Be("Scan.Drift.None");
    }

    [Fact]
    public async Task Une_seule_mesure_ne_dit_RIEN()
    {
        var vm = Vm(new PerformanceJournalStore(new FichierFake()), avgFps: 200);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.PerformanceDriftLabel.Should().BeNull("une premiere mesure n'a rien a comparer, et un "
                                                 + "zero rassurant serait une stabilite inventee");
    }

    [Fact]
    public async Task DEUX_MACHINES_ne_se_comparent_pas_a_l_ecran_non_plus()
    {
        // Un disque deplace, un dual-boot, un PC remplace : sans cette garde le produit
        // annoncerait « ta machine a perdu 40 % ».
        var f = new FichierFake();

        await Vm(new PerformanceJournalStore(f), avgFps: 200, machine: "PC-A").ScanCommand.ExecuteAsync(null);
        var vm = Vm(new PerformanceJournalStore(f), avgFps: 120, machine: "PC-B");
        await vm.ScanCommand.ExecuteAsync(null);

        vm.PerformanceDriftLabel.Should().BeNull();
    }

    [Fact]
    public async Task DEUX_JEUX_ne_se_comparent_pas_davantage_que_deux_machines()
    {
        // Le piege que ce test fige. Le produit mesure le jeu qui tourne, quel qu'il soit. Un
        // joueur qui scanne pendant un titre a 200 fps puis pendant un autre a 60 remplissait le
        // journal de deux echelles sans rapport — et l'ecran annonçait « ta machine rend 70 % de
        // moins ». Une degradation qui n'etait qu'un changement de jeu.
        var f = new FichierFake();

        await Vm(new PerformanceJournalStore(f), avgFps: 200, jeu: "warzone").ScanCommand.ExecuteAsync(null);
        var vm = Vm(new PerformanceJournalStore(f), avgFps: 60, jeu: "un-autre-jeu");
        await vm.ScanCommand.ExecuteAsync(null);

        vm.PerformanceDriftLabel.Should().BeNull("mieux vaut se taire que d'accuser la machine");
    }

    [Fact]
    public async Task Un_jeu_NON_IDENTIFIE_ne_se_compare_a_rien()
    {
        var f = new FichierFake();

        await Vm(new PerformanceJournalStore(f), avgFps: 200, jeu: "warzone").ScanCommand.ExecuteAsync(null);
        var vm = Vm(new PerformanceJournalStore(f), avgFps: 120, jeu: null);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.PerformanceDriftLabel.Should().BeNull("« inconnu » n'est pas « le meme jeu »");
    }

    [Fact]
    public async Task UNE_mesure_d_un_autre_jeu_ne_rend_pas_le_journal_MUET_pour_toujours()
    {
        // On filtre au lieu de refuser : une partie sur un autre titre au milieu de l'historique
        // ne doit pas condamner la comparaison du jeu qu'on mesure aujourd'hui.
        var f = new FichierFake();

        await Vm(new PerformanceJournalStore(f), avgFps: 200, jeu: "warzone").ScanCommand.ExecuteAsync(null);
        await Vm(new PerformanceJournalStore(f), avgFps: 60, jeu: "un-autre-jeu").ScanCommand.ExecuteAsync(null);
        var vm = Vm(new PerformanceJournalStore(f), avgFps: 160, jeu: "warzone");
        await vm.ScanCommand.ExecuteAsync(null);

        vm.PerformanceDriftLabel.Should().Be("Scan.Drift.Regression",
            "la comparaison porte sur les deux mesures de WARZONE, l'autre titre etant ecarte");
    }

    [Fact]
    public async Task Sans_journal_du_tout_la_ligne_n_existe_pas()
    {
        var vm = Vm(journal: null, avgFps: 200);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.PerformanceDriftLabel.Should().BeNull();
    }

    [Fact]
    public async Task Un_journal_qui_REFUSE_d_ecrire_ne_fait_pas_echouer_le_scan()
    {
        // Le journal est un confort de diagnostic. Faire echouer un scan parce qu'un fichier
        // n'a pas pu s'ecrire punirait l'utilisateur d'un probleme qui n'est pas le sien -- le
        // meme defaut que la mesure memoire avait, corrige le 07/09.
        var vm = Vm(new PerformanceJournalStore(new FichierFake { EchecEcriture = true }), avgFps: 200);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task Sans_journal_du_tout_le_scan_se_deroule_normalement()
    {
        // La couture est optionnelle : le harnais AMD et les previsualisations n'en ont pas.
        var vm = Vm(journal: null, avgFps: 200);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task Une_mesure_en_MENU_ne_sert_pas_d_etalon_a_une_mesure_en_PARTIE()
    {
        // 17/09/2026 : la scene declaree avant le scan ne voyageait pas avec la mesure. Un lobby a
        // 300 FPS puis une partie a 136 aurait ete lu comme une chute de 55 %.
        var f = new FichierFake();

        var lobby = Vm(new PerformanceJournalStore(f), avgFps: 300);
        lobby.SceneDeclaree = lobby.ChoixDeScene.First(c => c.Valeur == SceneRepresentativity.MenuLobby);
        await lobby.ScanCommand.ExecuteAsync(null);

        var partie = Vm(new PerformanceJournalStore(f), avgFps: 136);
        partie.SceneDeclaree = partie.ChoixDeScene.First(c => c.Valeur == SceneRepresentativity.Gameplay);
        await partie.ScanCommand.ExecuteAsync(null);

        partie.PerformanceDriftLabel.Should().BeNull("un menu et une partie ne se comparent pas");
        new PerformanceJournalStore(f).Lire().Select(e => e.Scene).Should()
            .BeEquivalentTo(new[] { SceneRepresentativity.MenuLobby, SceneRepresentativity.Gameplay });
    }
}
