using System.Collections.Generic;
using System.Threading;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

public class ScanViewModelDisplayTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "AMD", 16L << 30, "1", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    private static ScanViewModel BuildWithBaseline(BenchmarkResult? baseline)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = Substitute.For<IBenchmarkRunner>();
        if (baseline is not null)
            br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Result<BenchmarkResult>.Ok(baseline));
        else
            br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable, "pmon unavailable"));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), 72, baseline,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(baseline?.EstimatedPcLatencyMs != null, false))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key);
    }

    [Fact]
    public async Task Happy_path_sets_numeric_display_strings()
    {
        var baseline = new BenchmarkResult(142.7, 98.3, 75.0, new List<double> { 7.0 }, null, 2.1, 5);
        var vm = BuildWithBaseline(baseline);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.AvgFpsDisplay.Should().Be("143");
        vm.P1LowDisplay.Should().Be("98");
        vm.MarginDisplay.Should().Be("± 2.1");
        // 11/09/2026, demande produit : Jamais un chiffre seul. C'est cette propriete qui est liee au
        // grand chiffre de l'ecran, et elle ne peut pas exister sans sa marge.
        vm.AvgFpsAvecMarge.Should().Be("143 ± 2.1");
    }

    [Fact]
    public async Task Le_FPS_moyen_montre_a_l_ecran_porte_toujours_sa_marge()
    {
        var baseline = new BenchmarkResult(142.7, 98.3, 75.0, new List<double> { 7.0 }, null, 2.1, 5);
        var vm = BuildWithBaseline(baseline);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.AvgFpsAvecMarge.Should().Contain(vm.AvgFpsDisplay).And.Contain(vm.MarginDisplay);
        vm.AvgFpsAvecMarge.Should().NotBe(vm.AvgFpsDisplay, "un chiffre seul est trompeur");
    }

    // 27/09/2026 : le 1 % low porte sa propre marge, comme la moyenne ; inconnue, elle se dit.
    [Fact]
    public async Task Le_1_pourcent_low_porte_sa_propre_marge()
    {
        var mesuree = new BenchmarkResult(142.7, 98.3, 75.0, new List<double> { 7.0 }, null, 2.1, 5, P1LowMarginOfError: 4.5);
        var vm = BuildWithBaseline(mesuree);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.P1LowAvecMarge.Should().Be("98 ± 4.5");

        var sansMarge = BuildWithBaseline(new BenchmarkResult(142.7, 98.3, 75.0, new List<double> { 7.0 }, null, 2.1, 5));
        await sansMarge.ScanCommand.ExecuteAsync(null);
        sansMarge.P1LowAvecMarge.Should().Be("98 Dashboard.Marge.Inconnue").And.NotContain("Infinity");
    }

    // 26/09/2026 : sous trois passes la marge vaut +inf, et l'ecran ecrivait « 143 ± Infinity »
    // (symbole de la culture invariante). Meme phrase que le tableau de bord : marge inconnue.
    [Fact]
    public async Task Une_marge_infinie_se_dit_inconnue_jamais_Infinity()
    {
        var baseline = new BenchmarkResult(142.7, 98.3, 75.0, new List<double> { 7.0 }, null, double.PositiveInfinity, 1);
        var vm = BuildWithBaseline(baseline);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.MarginDisplay.Should().Be("Dashboard.Marge.Inconnue");
        vm.AvgFpsAvecMarge.Should().Be("143 Dashboard.Marge.Inconnue").And.NotContain("Infinity").And.NotContain("∞");
    }

    [Fact]
    public async Task Degraded_null_baseline_shows_dash_for_all_displays()
    {
        var vm = BuildWithBaseline(null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.AvgFpsDisplay.Should().Be("—");
        vm.P1LowDisplay.Should().Be("—");
        vm.MarginDisplay.Should().Be("—");
        vm.AvgFpsAvecMarge.Should().Be("—");
        vm.State.Should().Be(ScanState.Degraded);
    }

    [Fact]
    public void Display_strings_are_dash_before_scan()
    {
        var vm = BuildWithBaseline(null);

        vm.AvgFpsDisplay.Should().Be("—");
        vm.P1LowDisplay.Should().Be("—");
        vm.MarginDisplay.Should().Be("—");
    }

    [Fact]
    public async Task Rounding_is_invariant_culture_no_comma_separator()
    {
        var baseline = new BenchmarkResult(99.5, 61.4, 50.0, new List<double> { 10.0 }, null, 1.55, 3);
        var vm = BuildWithBaseline(baseline);

        await vm.ScanCommand.ExecuteAsync(null);

        // Must be "100" not "100,0" or localized (99.5 rounds to 100 via banker's rounding)
        vm.AvgFpsDisplay.Should().Be("100");
        vm.P1LowDisplay.Should().Be("61");
        // Margin uses 0.# format — "1.6" not "1,6"
        vm.MarginDisplay.Should().Be("± 1.6");
        vm.MarginDisplay.Should().NotContain(",");
    }

    // --- Coverage honesty (24/08) --------------------------------------------------------------
    // The scorer already computes ScanCoverage and it rode all the way to the UI unused, so a
    // machine whose levers could not be read still displayed "100 — in excellent health".
    // These tests pin the fix: the headline stops asserting when the read is incomplete.
    private static ScanViewModel BuildWithCoverage(int score, ScanCoverage? coverage)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable, "pmon unavailable"));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), score, null,
              new ScoreBreakdown(score, score, score), new DimensionStatus(false, false),
              Ledger: null, Coverage: coverage)));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key);
    }

    [Fact]
    public async Task Unread_levers_do_not_get_announced_as_excellent_health()
    {
        // The exact shape that made the scan feel untrue: nothing readable, so nothing deducted.
        var vm = BuildWithCoverage(100, new ScanCoverage(ApplicableLevers: 6, KnownLevers: 2, UnknownLevers: 4));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.IsCoverageComplete.Should().BeFalse();
        vm.VerdictText.Should().Be("UI.Verdict.Partial");
        vm.VerdictText.Should().NotBe("UI.Verdict.Excellent");
        vm.CoverageText.Should().Be("2/6");
    }

    [Fact]
    public async Task Full_coverage_keeps_the_normal_verdict()
    {
        var vm = BuildWithCoverage(100, new ScanCoverage(ApplicableLevers: 6, KnownLevers: 6, UnknownLevers: 0));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.IsCoverageComplete.Should().BeTrue();
        // Le verdict ne suit plus le score depuis le 05/09 : couverture complete et grand livre
        // vide -> « rien a corriger dans ce qui a ete lu ». Un score de 100 ne le rend pas
        // « excellent » : c'est la lecture qui parle, pas la note.
        vm.VerdictText.Should().Be("UI.Verdict.Clean");
        vm.CoverageText.Should().Be("6/6");
    }

    [Fact]
    public async Task Absent_coverage_is_treated_as_incomplete_not_as_full()
    {
        // A scorer that reports no coverage at all must not be read as "everything was read".
        var vm = BuildWithCoverage(100, null);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.IsCoverageComplete.Should().BeFalse();
        vm.VerdictText.Should().Be("UI.Verdict.Partial");
    }
}
