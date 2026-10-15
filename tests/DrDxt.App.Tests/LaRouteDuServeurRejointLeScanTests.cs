using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.Platform;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// La route vers le serveur de la partie est mesurée pendant la même fenêtre que les
// images, sur le jeu repéré, et sa phrase rejoint la santé de la machine (domaine Réseau). Sans jeu, rien n'est mesuré.
public sealed class LaRouteDuServeurRejointLeScanTests
{
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

    private static (IHardwareDetector, ISettingsStateReader, IBenchmarkRunner, IScanScorer, IStorageDetector) Fakes(List<double>? images = null)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));
        var bl = new BenchmarkResult(120, 90, 70, images ?? new List<double> { 8.0 }, null, 1.0, 5);
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(ci => ci.ArgAt<int>(0) > 0
              ? Result<BenchmarkResult>.Ok(bl)
              : Result<BenchmarkResult>.Fail(ErrorCodes.PidInvalid, "aucun jeu"));
        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St, new List<Finding>(), null)));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        return (hd, sr, br, sc, sd);
    }

    private static readonly RepairFinding Route = new("RESEAU", "route vers le serveur de la partie", "14 ms", null,
        RepairStatus.Suspicious, "pertes au-delà de ta box");

    private static ScanViewModel Build(int? pid, List<int> appels)
    {
        var (hd, sr, br, sc, sd) = Fakes();
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns(pid);
        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key, gameLocator: locator,
            repairAudit: () => Array.Empty<RepairFinding>(),
            mesurerRoute: (p, _) => { appels.Add(p); return Task.FromResult<RepairFinding?>(Route); });
    }

    [Fact]
    public async Task Pendant_une_partie_la_route_du_jeu_est_mesuree_et_affichee()
    {
        var appels = new List<int>();
        var vm = Build(4242, appels);

        await vm.ScanCommand.ExecuteAsync(null);

        appels.Should().Equal(4242);
        vm.RepairFindings.Should().Contain(r => r.Explication.Contains("pertes au-delà de ta box"));
    }

    [Fact]
    public async Task Sans_jeu_la_route_n_est_pas_mesuree()
    {
        var appels = new List<int>();
        var vm = Build(null, appels);

        await vm.ScanCommand.ExecuteAsync(null);

        appels.Should().BeEmpty();
    }

    // La mémoire libre relevée pendant la même fenêtre, jugée avec les images de la même partie.
    [Fact]
    public async Task Memoire_basse_et_a_coups_en_partie_rejoignent_la_sante()
    {
        var (hd, sr, br, sc, sd) = Fakes([.. Enumerable.Repeat(7.0, 200), 30, 35, 40]);
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns(4242);
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key, gameLocator: locator,
            repairAudit: () => Array.Empty<RepairFinding>(),
            mesurerMemoireLibre: _ => Task.FromResult<ulong?>(600UL * 1024 * 1024));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.RepairFindings.Should().Contain(r => r.Explication.Contains("600 Mo"));
    }

    // « les 1 points » sous « 13 points à corriger ». La santé arrive après le premier
    // calcul de l'en-tête ; il doit suivre le compte final, celui que la carte et le résumé affichent.
    [Fact]
    public async Task L_en_tete_a_faire_suit_le_compte_final_avec_la_sante()
    {
        var (hd, sr, br, sc, sd) = Fakes();
        var locator = Substitute.For<IGameLocator>();
        locator.LocateRunningGamePid().Returns(4242);
        RepairFinding Grave(string nom) => new("WINDOWS", nom, null, null, RepairStatus.KnownHarmful, "à corriger");
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, k => k == "Carte.AFaire.Total" ? "total {0}" : k,
            gameLocator: locator, repairAudit: () => [Grave("a"), Grave("b"), Grave("c")]);
        string? vuParLEcran = null;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ScanViewModel.LabelAFaire)) vuParLEcran = vm.LabelAFaire; };

        await vm.ScanCommand.ExecuteAsync(null);

        var attendu = vm.RepairFindings.Count(r => r.Severity > Severity.Info);
        attendu.Should().BeGreaterThan(1, "témoin : la santé a bien apporté plusieurs points");
        vuParLEcran.Should().Be($"total {attendu}", "l'écran ne relit que ce qu'on lui notifie");
        vm.ProblemSummary.Should().Contain(attendu.ToString());
    }

    // La version du pilote, vérifiée avec les cartes que le scan a détectées, rejoint la santé.
    [Fact]
    public async Task La_verification_du_pilote_recoit_les_cartes_et_rejoint_la_sante()
    {
        var (hd, sr, br, sc, sd) = Fakes();
        IReadOnlyList<GpuInfo>? recues = null;
        var vm = ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key,
            repairAudit: () => Array.Empty<RepairFinding>(),
            verifierPilote: g => { recues = g; return Task.FromResult<RepairFinding?>(new("GPU DRIVER", "version du pilote graphique",
                "NVIDIA 616.56", "617.14", RepairStatus.Suspicious, "3 versions de retard")); });

        await vm.ScanCommand.ExecuteAsync(null);

        recues.Should().ContainSingle(g => g.Model == "NVIDIA RTX 4060");
        vm.RepairFindings.Should().Contain(r => r.Explication.Contains("3 versions de retard"));
    }
}
