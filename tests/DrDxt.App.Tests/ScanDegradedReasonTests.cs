using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// « LANCE UNE PARTIE » est un mensonge quand le service est mort.
//
// Le scan disait une seule phrase des que la mesure en jeu echouait, quelle qu'en soit la cause :
// « Ta vraie perf en jeu n'est pas encore mesuree -- lance une partie, puis relance le scan. »
// Sur la machine de reference, la cause reelle etait le conflit de tube avec le service Intel
// (PM_STATUS_PIPE_ERROR, 3 fois sur 5, mesure du 02/09/2026). Le client lancait donc une partie,
// relancait le scan, et obtenait le meme message -- indefiniment. Un diagnostic qui accuse
// l'utilisateur de son propre defaut est pire qu'un silence.
//
// L'information EXISTAIT deja : `BenchmarkRunner` distingue `MEAS.PMON_UNAVAIL` (le service ne
// repond pas) de `MEAS.PID_INVALID` (aucun jeu a mesurer). Elle mourait dans un `bool degraded`.
// -----------------------------------------------------------------------
public sealed class ScanDegradedReasonTests
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

    private static ScanViewModel Build(string codeDEchec)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Fail(codeDEchec, "peu importe le texte interne"));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(
              Hw(), St, new List<Finding>(), 68, null,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(false, false))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        var store = Substitute.For<IDesiredStateStore>();
        store.CommitAsync(Arg.Any<DesiredState>()).Returns(Result.Ok());
        store.LoadAsync().Returns(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>())));

        // `key => key` : la note affichee est la cle, donc le test lit le choix, pas la traduction.
        return new ScanViewModel(
            hd, sr, br, sc, sd,
            ScanViewModelTestSeams.Safety(), ScanViewModelTestSeams.Engine(),
            ScanViewModelTestSeams.ProfileConsumer(), ScanViewModelTestSeams.GpuTuningStep(),
            ScanViewModelTestSeams.Navigation(), ScanViewModelTestSeams.DashboardFactory(),
            store, new AppliedTweakManifestMapper(),
            ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), key => key);
    }

    [Fact]
    public async Task Service_de_mesure_injoignable_le_scan_NE_dit_PAS_de_lancer_une_partie()
    {
        // Le cas du conflit de tube : lancer un jeu n'y change rien, et le dire est une fausse piste.
        var vm = Build(ErrorCodes.PmonUnavailable);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.DegradedNote.Should().Be("Scan.Degraded.ServiceUnavailable");
        vm.DegradedNote.Should().NotBe("Scan.Degraded.PresentMon");
    }

    [Fact]
    public async Task Aucun_jeu_a_mesurer_le_scan_dit_bien_de_lancer_une_partie()
    {
        // Le seul cas ou le message historique est vrai : il faut le garder intact.
        var vm = Build(ErrorCodes.PidInvalid);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.DegradedNote.Should().Be("Scan.Degraded.PresentMon");
    }

    [Fact]
    public async Task Le_scan_aboutit_dans_les_deux_cas_au_lieu_d_echouer()
    {
        // La note explique une couverture manquante ; elle ne doit pas faire echouer le scan.
        // `Degraded` -- pas `Failed` : le reste du diagnostic (materiel, reglages) est valide.
        foreach (var code in new[] { ErrorCodes.PmonUnavailable, ErrorCodes.PidInvalid })
        {
            var vm = Build(code);
            await vm.ScanCommand.ExecuteAsync(null);
            vm.State.Should().Be(ScanState.Degraded, $"code {code}");
        }
    }
}
