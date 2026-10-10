using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// 10/10/2026 (le mainteneur : « c'est pas normal qu'Optimiser n'ait rien à poser chez moi ») : le mode d'alimentation
// NVIDIA au global est un constat que DrDXT sait corriger seul (« Rendre le repos »), mais le geste vivait à part dans
// « Agir ». Optimiser le fait d'abord, et le dit.
public sealed class OptimiserFaitLesGestesDuDocteurTests
{
    private static async Task<ScanViewModel> ScanneAvec(params Finding[] constats)
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
          .Returns(Result<ScanResult>.Ok(new ScanResult(hw, st, constats.ToList(), 68, null,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k == "Apply.GesteFait" ? "{0} : {1}" : k);
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

    // 10/10/2026 (« ça manque de conclusion ») : Optimiser finit par une ordonnance, jamais par une impasse.
    [Fact]
    public async Task L_ordonnance_dit_ce_qui_reste_a_faire_soi_meme_et_la_suite()
    {
        var vm = await ScanneAvec(new Finding("Gpu", Severity.Minor, "Finding.Gpu.PerfMaxGlobal", null));

        var ordonnance = vm.Ordonnance(reposFait: false);

        ordonnance.Should().StartWith("Ordonnance.Titre").And.Contain("Ordonnance.Suite");
        if (vm.PointsAFaire.Count > 0) ordonnance.Should().Contain("Ordonnance.ATaMain").And.Contain("Ordonnance.Guide");
        else ordonnance.Should().Contain("Ordonnance.RienAMain");
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
