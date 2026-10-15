using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using DrDxt.Detection.Network;
using DrDxt.Measurement.Network;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Le cablage de l'attribution reseau.
//
// Le defaut repare ici : `INetworkLatencyMeter` et `NetworkPathAnalyzer` etaient ecrits,
// testes, et appeles par `tools/netprobe` Seulement. Le produit lisait les reglages de la carte
// et ne mesurait rien du reseau — le domaine qu'un joueur competitif ressent avant tous les
// autres. Des tests verts sur un instrument que personne n'atteint ne prouvent rien du produit.
//
// Ces tests traversent la chaine entiere : sondes fictives -> analyseur -> service -> le VM de
// l'ecran. Et ils portent autant sur le SILENCE que sur la phrase.
// -----------------------------------------------------------------------
public sealed class ScanAttributesNetworkPathTests
{
    private sealed class CartesFictives(string? passerelle, string? secondeSortie = null)
        : INetworkAdapterReader
    {
        public Result<IReadOnlyList<string>> ReadAllDefaultGateways()
        {
            var toutes = new List<string>();
            if (passerelle is not null) toutes.Add(passerelle);
            if (secondeSortie is not null) toutes.Add(secondeSortie);
            return Result<IReadOnlyList<string>>.Ok(toutes);
        }

        public Result<IReadOnlyList<NetworkAdapterState>> Read()
            => Result<IReadOnlyList<NetworkAdapterState>>.Ok(
                [new NetworkAdapterState("Ethernet", "Realtek 2.5GbE", true,
                    TriState.Unknown, TriState.Unknown, TriState.Unknown)]);

        public Result<string?> ReadDefaultGateway(NetworkAdapterState adapter)
            => Result<string?>.Ok(passerelle);
    }

    private sealed class CompteurFictif(double? gigueSaut, double? gigueInternet) : INetworkLatencyMeter
    {
        private int _appels;

        public Task<Result<NetworkLatencyReport>> MeasureAsync(
            string host, int probeCount, TimeSpan interval, TimeSpan timeout, CancellationToken ct,
            int warmupProbes = NetworkLatencyMeter.DefaultWarmupProbes)
        {
            // Box puis Internet, à chaque comparaison : une accusation est comparée deux fois.
            var gigue = ++_appels % 2 == 1 ? gigueSaut : gigueInternet;
            return Task.FromResult(Result<NetworkLatencyReport>.Ok(
                new NetworkLatencyReport(host, 10, 10, 0, 12, 10, 20, 18, gigue)));
        }
    }

    private static HardwareInventory Hw() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
        new[] { new GpuInfo("NVIDIA RTX 4060", "NVIDIA", 8L << 30, "551", TriState.On) },
        new[] { new MemoryModule("PN", "Kingston", 3800, 4000) },
        new MotherboardInfo("Gigabyte", "B760"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static ScanViewModel Build(Func<CancellationToken, Task<string?>>? attribution)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var bl = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(bl));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St, new List<Finding>(), bl)));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        var store = Substitute.For<IDesiredStateStore>();
        store.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Ok());
        store.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));

        return new ScanViewModel(
            hd, sr, br, sc, sd,
            ScanViewModelTestSeams.Safety(), ScanViewModelTestSeams.Engine(),
            ScanViewModelTestSeams.ProfileConsumer(), store, new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key,
            attributionReseau: attribution);
    }

    private static Func<CancellationToken, Task<string?>> Chaine(
        double? gigueSaut, double? gigueInternet, string? passerelle = "192.168.1.1")
    {
        var service = new AttributionReseauService(
            new CartesFictives(passerelle), new CompteurFictif(gigueSaut, gigueInternet), "1.1.1.1");
        return ct => service.LigneAsync(ct);
    }

    [Fact]
    public async Task Le_scan_dit_a_l_ecran_D_OU_vient_l_instabilite()
    {
        var vm = Build(Chaine(gigueSaut: 6.0, gigueInternet: 1.0));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.NetworkPathLabel.Should().NotBeNull();
        vm.NetworkPathLabel.Should().Contain("premier saut").And.Contain("192.168.1.1");
        vm.NetworkPathLabel.Should().Contain("piste à confirmer",
            "le caveat voyage avec la phrase, jamais en note de bas de page");
    }

    [Fact]
    public async Task Sans_passerelle_lisible_l_ecran_ne_dit_RIEN_du_reseau()
    {
        var vm = Build(Chaine(6.0, 1.0, passerelle: null));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.NetworkPathLabel.Should().BeNull("la ligne disparait ; « non mesure » ne se remplace pas "
                                            + "par « reseau sain »");
    }

    [Fact]
    public async Task Une_attribution_qui_LEVE_ne_fait_pas_echouer_le_scan()
    {
        // Meme garde que partout ailleurs : mesurer le reseau ne doit jamais etre la raison pour
        // laquelle un scan s'arrete.
        var vm = Build(_ => throw new InvalidOperationException("pile reseau indisponible"));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.NetworkPathLabel.Should().BeNull();
        vm.AssistantSummary.Should().NotBeNullOrEmpty("le reste du scan doit avoir abouti");
    }

    [Fact]
    public async Task Sans_couture_branchee_le_produit_se_comporte_comme_avant()
    {
        var vm = Build(null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.NetworkPathLabel.Should().BeNull();
        vm.AssistantSummary.Should().NotBeNullOrEmpty();
    }
}
