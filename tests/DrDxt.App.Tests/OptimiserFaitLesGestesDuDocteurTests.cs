using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// Le mode d'alimentation
// NVIDIA au global est un constat que DrDXT sait corriger seul (« Rendre le repos »), mais le geste vivait à part dans
// « Agir ». Optimiser le fait d'abord, et le dit.
public sealed class OptimiserFaitLesGestesDuDocteurTests
{
    private static Task<ScanViewModel> ScanneAvec(params Finding[] constats) => ScanneAvec(k => k == "Apply.GesteFait" ? "{0} : {1}" : k, constats);

    private static async Task<ScanViewModel> ScanneAvec(Func<string, string> loc, params Finding[] constats)
    {
        var hw = new HardwareInventory(
            new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
            new[] { new GpuInfo("NVIDIA RTX 4060", "NVIDIA", 8L << 30, "551", TriState.On) },
            new[] { new MemoryModule("PN", "Kingston", 3800, 4000) },
            new MotherboardInfo("MSI", "B760"), new BiosInfo("1", DateTimeOffset.UtcNow, true), ChassisType.Desktop);
        var st = new SettingsState(TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(hw));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(st));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.PidInvalid, "aucun jeu"));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(hw, st, constats.ToList(), null)));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, loc);
        await vm.ScanCommand.ExecuteAsync(null);
        return vm;
    }

    private static ITweak Levier(ScanViewModel vm)
    {
        var levier = Substitute.For<ITweak>();
        levier.Id.Returns("gpu.repos_global_normal");
        levier.ApplyAsync().Returns(Result<TweakSnapshot>.Ok(new TweakSnapshot("gpu.repos_global_normal", "inverse")));
        var sonde = Substitute.For<IHardwareProbe>();
        sonde.Lire().Returns(new HardwareReading(55.8, null));
        vm.LevierReposCarte = levier;
        vm.SondeCarte = sonde;
        vm.AttenteReposCarte = new DrDxt.Application.Tweaks.PreuvesEnAttente(Path.Combine(Path.GetTempPath(), $"preuve-{Guid.NewGuid():N}.json"));
        vm.AttendreCarte = _ => Task.CompletedTask;
        return levier;
    }

    [Fact]
    public async Task Le_mode_nvidia_au_global_est_rendu_au_repos_par_optimiser_et_c_est_dit()
    {
        var vm = await ScanneAvec(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null));
        var levier = Levier(vm);

        var lignes = await vm.GestesDuDocteurAsync();

        await levier.Received(1).ApplyAsync();
        lignes.Should().ContainSingle().Which.Should().Contain("Carte.Repos.EnAttente", "le résultat dit ce qui a été fait, et la suite");
        (await vm.GestesDuDocteurAsync()).Should().HaveCount(0, "déjà en attente de sa preuve : rien à refaire");
    }

    // Optimiser finit par une ordonnance, jamais par une impasse.
    [Fact]
    public async Task L_ordonnance_dit_ce_qui_reste_a_faire_soi_meme_et_la_suite()
    {
        var vm = await ScanneAvec(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null));

        var ordonnance = vm.Ordonnance(reposFait: false);

        ordonnance.Should().StartWith("Ordonnance.Titre").And.Contain("Ordonnance.Suite");
        if (vm.PointsAFaire.Count > 0) ordonnance.Should().Contain("Ordonnance.ATaMain").And.Contain("Ordonnance.Guide");
        else ordonnance.Should().Contain("Ordonnance.RienAMain");
    }

    // L'ordonnance compte tout ce qui reste à la main, pas les trois points visibles sous le verdict.
    [Fact]
    public async Task L_ordonnance_compte_tous_les_points_a_la_main()
    {
        var vm = await ScanneAvec(k => k == "Ordonnance.ATaMain" ? "à ta main ({0})" : k,
            new Finding("Gpu", Severity.Major, "Finding.A", null), new Finding("Cpu", Severity.Major, "Finding.B", null),
            new Finding("Memory", Severity.Major, "Finding.C", null), new Finding("Network", Severity.Major, "Finding.D", null),
            new Finding("Storage", Severity.Major, "Finding.E", null));
        vm.PointsAFaire.Should().HaveCount(3, "témoin : la liste visible est plafonnée");

        vm.Ordonnance(reposFait: false).Should().Contain("à ta main (5)");
    }

    // Après le scan, une prochaine étape en tête, un seul bouton.
    [Fact]
    public async Task Apres_le_scan_le_docteur_dit_la_prochaine_etape_et_elle_suit_ce_qui_est_fait()
    {
        var vm = await ScanneAvec(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null));
        Levier(vm);
        vm.SignalerReposCarte();
        vm.ProchaineEtape.Should().Be("Etape.Optimiser", "DrDXT a un geste à faire seul : c'est Optimiser d'abord");
        vm.EtapeHorsListe.Should().Be("Etape.Optimiser", "rien à faire soi-même : l'encadré dit l'étape");
        vm.RienAPoserSeul = true;
        vm.RienAPoserDuTout.Should().BeFalse("plus de « rien à poser par Optimiser » quand Optimiser a un geste à faire");
        vm.ApplyHintKey.Should().NotBe("Apply.RienAPoserSeul");
        var annonces = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ScanViewModel.ProchaineEtape)) annonces++; };

        await vm.GestesDuDocteurAsync();

        vm.ReposCarteEnAttente.Should().BeTrue();
        annonces.Should().BeGreaterThan(0, "l'écran suit");
        vm.ProchaineEtape.Should().BeOneOf("Etape.AFaire", "Etape.Jouer");
    }

    // Après Optimiser, l'étape en tête ne dit plus « Optimiser », ni « DrDXT ne peut pas le faire à ta
    // place » pour le réglage qu'il vient de faire. Et le geste s'écrit après le point de restauration annoncé.
    [Fact]
    public async Task Apres_optimiser_l_etape_suit_ce_qui_a_ete_fait_et_le_point_de_restauration_vient_avant()
    {
        var vm = await ScanneAvec(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null));
        var levier = Levier(vm);
        var ordre = new List<string>();
        vm.Safety.EnsureRestorePointAsync().Returns(_ => { ordre.Add("point"); return Result.Ok(); });
        levier.ApplyAsync().Returns(_ => { ordre.Add("geste"); return Result<TweakSnapshot>.Ok(new TweakSnapshot("gpu.repos_global_normal", "inverse")); });
        vm.SignalerReposCarte();
        vm.ProchaineEtape.Should().Be("Etape.Optimiser");

        await vm.ApplyCommand.ExecuteAsync(null);

        ordre.Should().Equal(new[] { "point", "geste" }, "le point de restauration passe avant tout ce qui écrit");
        vm.ReposCarteEnAttente.Should().BeTrue();
        vm.ResultatOptimisation.Should().Contain("Carte.Repos.EnAttente").And.Contain("Ordonnance.Titre");
        vm.ProchaineEtape.Should().Be("Etape.Jouer", "le seul point à faire vient d'être fait par DrDXT : plus rien à sa main ni à la tienne");
        vm.ApplyBlockedMessage.Should().BeNull();
    }

    // 10/10/2026 soir (« c'est relou que DrDXT ne s'occupe pas plus des points à corriger avant opti ») : ce qui reste à la
    // main du joueur s'écrit en tête du résultat, avant ce qu'Optimiser a fait ; et l'étape le dit en premier, puis Optimiser.
    [Fact]
    public async Task Ce_qui_est_a_ta_main_vient_en_tete_et_l_etape_le_dit_avant_optimiser()
    {
        var vm = await ScanneAvec(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null),
            new Finding("Latency", Severity.Minor, "Finding.Latency.VrrOff", null));
        Levier(vm);
        vm.SignalerReposCarte();
        vm.PointsALaMain.Should().ContainSingle(p => p.Entree.Cle == "Finding.Latency.VrrOff",
            "le repos de la carte, Optimiser le fait lui-même : il n'est pas à ta main");
        vm.ProchaineEtape.Should().Be("Etape.AFairePuisOptimiser");
        vm.EtapeHorsListe.Should().Be("Etape.PuisOptimiser", "le point à la main est en tête de liste ; l'encadré garde « puis Optimise »");
        vm.RienAPoserDuTout.Should().BeFalse("Optimiser a toujours sa part à faire");

        await vm.ApplyCommand.ExecuteAsync(null);

        var r = vm.ResultatOptimisation!;
        r.Should().StartWith("Ordonnance.Titre");
        r.IndexOf("Ordonnance.ATaMain", StringComparison.Ordinal).Should().BeLessThan(r.IndexOf("Carte.Repos.EnAttente", StringComparison.Ordinal),
            "ce qui est à ta main s'écrit avant ce qu'Optimiser a fait");
        r.Should().EndWith("Ordonnance.Suite");
        vm.ProchaineEtape.Should().Be("Etape.AFaire", "le repos est fait par DrDXT ; reste le VRR, à ta main");
    }

    // Le point à faire soi-même est déjà le premier de la liste, cliquable vers son domaine : un encadré qui le redit
    // au-dessus ferait lire deux fois la même chose. L'encadré ne garde que ce que la liste ne dit pas : « puis Optimise ».
    [Fact]
    public async Task L_etape_a_ta_main_se_lit_dans_la_liste_et_pas_dans_un_encadre()
    {
        var vm = await ScanneAvec(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null),
            new Finding("Latency", Severity.Minor, "Finding.Latency.VrrOff", null));
        Levier(vm);
        vm.SignalerReposCarte();
        vm.ProchaineEtape.Should().Be("Etape.AFairePuisOptimiser", "témoin : l'étape est bien à ta main");
        vm.EtapeHorsListe.Should().Be("Etape.PuisOptimiser", "la liste dit déjà ce point, en premier ; l'encadré dit seulement la suite");

        var seul = await ScanneAvec(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null));
        Levier(seul);
        seul.SignalerReposCarte();
        seul.ProchaineEtape.Should().Be("Etape.Optimiser");
        seul.EtapeHorsListe.Should().Be("Etape.Optimiser", "rien à ta main : l'encadré dit la suite que la liste ne dit pas");
    }

    [Fact]
    public async Task Un_clic_qui_n_a_rien_a_poser_ne_cree_pas_de_point_de_restauration()
    {
        var vm = await ScanneAvec();
        await vm.ApplyCommand.ExecuteAsync(null);
        await vm.Safety.DidNotReceive().EnsureRestorePointAsync();
        vm.ResultatOptimisation.Should().Contain("Ordonnance.Titre", "jamais d'impasse sans suite");
    }

    [Fact]
    public void Avant_le_scan_pas_d_etape()
    {
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k);
        vm.ProchaineEtape.Should().BeNull();
        vm.EtapeHorsListe.Should().BeNull();
    }

    [Fact]
    public async Task Sans_le_constat_optimiser_ne_touche_pas_au_pilote()
    {
        var vm = await ScanneAvec();
        var levier = Levier(vm);

        (await vm.GestesDuDocteurAsync()).Should().HaveCount(0);
        await levier.DidNotReceive().ApplyAsync();
    }
}
