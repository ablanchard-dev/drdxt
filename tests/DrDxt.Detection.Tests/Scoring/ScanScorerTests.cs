using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Scoring;

public class ScanScorerTests
{
    private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    // Driver "24.10.1" → branch 24 ≥ floor 20 → FRESH (a clean fixture scores 100).
    private static HardwareInventory Hw(bool x3d = false, int ccd = 1, ChassisType chassis = ChassisType.Desktop,
        string vendor = "AMD", TriState rebar = TriState.On, string driver = "24.10.1") => new(
        new CpuInfo("CPU", "AMD", 8, 16, x3d, ccd),
        new[] { new GpuInfo("GPU", vendor, 16L<<30, driver, rebar) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"), new BiosInfo("1", DateTimeOffset.UtcNow, true), chassis);

    // L1.4 : `displayRefresh` par defaut = ecran LU et deja a son maximum. C'est ce que « machine
    // propre » veut dire depuis que le levier de frequence d'affichage existe -- un ecran dont les
    // modes n'ont pas pu etre enumeres est honnetement NON COUVERT, et le laisser a null ferait
    // porter a tous ces tests une couverture incomplete qui n'est pas leur sujet.
    private static SettingsState State(TriState xmp = TriState.On, string power = HighPerf,
        TriState hags = TriState.On, TriState gameMode = TriState.On, int[]? parked = null,
        TriState vrr = TriState.Unknown, TriState vrrCapable = TriState.Unknown, TriState rebar = TriState.On,
        RefreshHeadroom? displayRefresh = null,
        // L1.5 : idem pour les degats d'un optimiseur precedent. « Machine propre » veut dire
        // pagefile present et ordonnanceur multimedia intact, tous deux LUS — les laisser a
        // Unknown ferait porter a tous ces tests une couverture incomplete qui n'est pas leur
        // sujet, et masquerait la vraie regression le jour ou elle arriverait.
        TriState pagefile = TriState.On, TriState mmcss = TriState.On) =>
        new(xmp, rebar, power, hags, gameMode, parked ?? Array.Empty<int>(), vrr, vrrCapable,
            PagefileConfigured: pagefile,
            DisplayRefresh: displayRefresh ?? new RefreshHeadroom(TriState.Off, 200, 200),
            MmcssIntact: mmcss);

    private static BenchmarkResult Baseline => new(120, 90, 70, new List<double> { 8.0 }, null, 1.0, 5);

    [Fact]
    public void Score_returns_ok_result()
    {
        new ScanScorer().Score(Hw(), State(), Baseline).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Well_tuned_machine_scores_100()
    {
        // Clean machine (XMP on, High-Perf, fresh driver, ReBar on, no X3D misconfig) → no penalty.
        var r = new ScanScorer().Score(Hw(), State(), Baseline).Value!;
        r.Score.Should().Be(100);
    }

    [Fact]
    public void Xmp_off_lowers_score()
    {
        var r = new ScanScorer().Score(Hw(), State(xmp: TriState.Off), Baseline).Value!;
        r.Score.Should().Be(85); // only the real lever deducts (15)
        r.Findings.Should().Contain(x => x.Category == "Memory");
    }

    [Fact]
    public void Score_is_clamped_between_0_and_100()
    {
        var bad = State(xmp: TriState.Off, power: Balanced, hags: TriState.Off);
        var r = new ScanScorer().Score(Hw(x3d: true, ccd: 2), bad, Baseline).Value!;
        r.Score.Should().BeInRange(0, 100);
    }

    [Fact]
    public void Result_carries_hw_state_findings_baseline()
    {
        var hw = Hw(); var st = State(); var bl = Baseline;
        var r = new ScanScorer().Score(hw, st, bl).Value!;
        r.Hw.Should().BeSameAs(hw);
        r.State.Should().BeSameAs(st);
        r.Baseline.Should().BeSameAs(bl);
    }

    [Fact]
    public void Degraded_mode_null_baseline_still_scores()
    {
        // PresentMon indispo → baseline null, le scan produit quand même un score.
        var r = new ScanScorer().Score(Hw(), State(), baseline: null);
        r.IsOk.Should().BeTrue();
        r.Value!.Baseline.Should().BeNull();
    }

    [Fact]
    public void Info_only_findings_do_not_drag_score_below_90_on_clean_machine()
    {
        // 16/09/2026 : « Latence : optimisations disponibles pour ta configuration » etait ajoute a
        // Toute machine, sans rien lire. Un constat qui ne vient d'aucune lecture n'a pas sa place.
        var r = new ScanScorer().Score(Hw(), State(), Baseline).Value!;
        r.Findings.Should().NotContain(x => x.TitleKey == "Finding.Latency.Available");
        r.Score.Should().Be(100);
    }

    // --- audit C2 : neutral Windows defaults must not fabricate a deficit. -----------------------

    [Fact]
    public void Neutral_windows_defaults_do_not_lower_the_score()
    {
        // HAGS off + Balanced (no symptom) + Game Mode off + VRR off on a non-confirmed display:
        // ALL neutral. Only real, gated levers deduct → none here → 100. This is THE C2 correction:
        // the old model dropped stock-OEM Windows to ~80 by punishing these neutrals.
        var st = State(xmp: TriState.On, power: Balanced, hags: TriState.Off, gameMode: TriState.Off,
            vrr: TriState.Off, vrrCapable: TriState.Unknown);
        var r = new ScanScorer().Score(Hw(), st, null).Value!;
        r.Score.Should().Be(100);
    }

    [Fact]
    public void Stock_oem_only_deducts_the_real_lever_not_the_neutrals()
    {
        // XMP off (the one real lever) + Balanced + HAGS off + VRR unknown + ReBar unknown, no X3D.
        // audit C2/M5/H2 : only XMP (15) deducts → 85 — not 80 (the neutrals are no longer punished).
        var hw = Hw(vendor: "NVIDIA", rebar: TriState.Unknown, driver: "560.94");
        var st = State(xmp: TriState.Off, power: Balanced, hags: TriState.Off, gameMode: TriState.Off,
            vrr: TriState.Unknown, vrrCapable: TriState.Unknown);
        var r = new ScanScorer().Score(hw, st, null).Value!;
        r.Score.Should().Be(85);
    }

    [Fact]
    public void Well_tuned_machine_scores_90_plus()
    {
        var r = new ScanScorer().Score(Hw(), State(vrr: TriState.On, vrrCapable: TriState.On), null).Value!;
        r.Score.Should().BeGreaterThanOrEqualTo(90);
    }

    [Fact]
    public void Sub_scores_are_in_valid_range()
    {
        var r = new ScanScorer().Score(Hw(), State(), Baseline).Value!;
        r.Breakdown.Performance.Should().BeInRange(0, 100);
        r.Breakdown.Latency.Should().BeInRange(0, 100);
        r.Breakdown.Stability.Should().BeInRange(0, 100);
    }

    // --- GPU driver lever (was invisible to the score). -----------------------------------------

    [Fact]
    public void Stale_gpu_driver_lowers_score_by_major_magnitude()
    {
        var r = new ScanScorer().Score(Hw(driver: "9.1"), State(), null).Value!;
        r.Score.Should().Be(85); // Major −15
        r.Breakdown.Performance.Should().Be(85);
        r.Findings.Should().Contain(x => x.TitleKey == "Finding.Gpu.DriverStale");
    }

    // --- audit M12 : X3D penalized only on confirmed misconfig. ----------------------------------

    [Fact]
    public void X3d_dual_ccd_without_parking_symptom_is_not_penalized()
    {
        var r = new ScanScorer().Score(Hw(x3d: true, ccd: 2), State(), null).Value!;
        r.Score.Should().Be(100); // Unknown → 0, never punish a healthy X3D machine
        r.Findings.Should().Contain(x => x.TitleKey == "Finding.Scheduling.X3dUnknown");
    }

    [Fact]
    public void X3d_dual_ccd_with_parking_symptom_is_penalized()
    {
        var r = new ScanScorer().Score(Hw(x3d: true, ccd: 2), State(power: HighPerf, parked: new[] { 0, 1 }), null).Value!;
        r.Score.Should().Be(88); // Major −12
    }

    // --- Lot 1 (10/09/2026) : le plan d'alimentation n'est plus un levier du registre. -----------

    [Fact]
    public void Balanced_with_parked_cores_no_longer_lowers_stability()
    {
        // Le parking est identique entre Equilibre et Haute performance : ce n'etait pas un defaut.
        var r = new ScanScorer().Score(Hw(), State(power: Balanced, parked: new[] { 2 }), null).Value!;
        r.Score.Should().Be(100);
        r.Breakdown.Stability.Should().Be(100);
    }

    [Fact]
    public void Balanced_without_symptom_does_not_lower_stability()
    {
        var r = new ScanScorer().Score(Hw(), State(power: Balanced), null).Value!;
        r.Score.Should().Be(100);
        r.Breakdown.Stability.Should().Be(100);
    }

    // --- audit H2 : VRR off gated on a VRR-capable display. --------------------------------------

    [Fact]
    public void Vrr_off_with_capable_display_lowers_latency_sub_score()
    {
        var stateVrrOn = State(vrr: TriState.On, vrrCapable: TriState.On);
        var stateVrrOff = stateVrrOn with { Vrr = TriState.Off };

        var rOn = new ScanScorer().Score(Hw(), stateVrrOn, null).Value!;
        var rOff = new ScanScorer().Score(Hw(), stateVrrOff, null).Value!;

        rOff.Findings.Should().Contain(x => x.TitleKey == "Finding.Latency.VrrOff");
        rOff.Breakdown.Latency.Should().Be(95);
        rOff.Breakdown.Latency.Should().BeLessThan(rOn.Breakdown.Latency);
        rOff.Score.Should().BeLessThan(rOn.Score);
    }

    [Fact]
    public void Vrr_off_without_capable_display_does_not_penalize()
    {
        // audit H2 : no confirmed VRR display → 0 penalty (a 60 Hz non-VRR screen is correctly set).
        var r = new ScanScorer().Score(Hw(), State(vrr: TriState.Off, vrrCapable: TriState.Unknown), null).Value!;
        r.Score.Should().Be(100);
        r.Breakdown.Latency.Should().Be(100);
        // still surfaced as a neutral opportunity finding
        r.Findings.Should().Contain(x => x.TitleKey == "Finding.Latency.VrrInactiveSupportUnknown" && x.Severity == Severity.Info);
    }

    [Fact]
    public void Vrr_unknown_no_penalty_finding_is_info()
    {
        var stateVrrOn = State(vrr: TriState.On, vrrCapable: TriState.On);
        var stateVrrUnknown = stateVrrOn with { Vrr = TriState.Unknown };

        var rOn = new ScanScorer().Score(Hw(), stateVrrOn, null).Value!;
        var rUnknown = new ScanScorer().Score(Hw(), stateVrrUnknown, null).Value!;

        // Unknown must not penalize score or latency sub-score
        rUnknown.Score.Should().Be(rOn.Score);
        rUnknown.Breakdown.Latency.Should().Be(rOn.Breakdown.Latency);

        var vrrFinding = rUnknown.Findings.FirstOrDefault(x => x.TitleKey == "Finding.Latency.VrrUnknown");
        vrrFinding.Should().NotBeNull();
        vrrFinding!.Severity.Should().Be(Severity.Info);
    }

    // --- audit H3 + coverage : Unknown costs 0 but is visible. -----------------------------------

    [Fact]
    public void Nvidia_rebar_unknown_keeps_score_100_but_pushes_guidance_and_marks_coverage()
    {
        var r = new ScanScorer().Score(Hw(vendor: "NVIDIA", rebar: TriState.Unknown, driver: "560.94"), State(), null).Value!;
        r.Score.Should().Be(100); // Unknown never penalizes
        r.Findings.Should().Contain(x => x.TitleKey == "Finding.Gpu.ReBarGuidance"); // H3 guidance
        r.Coverage.Should().NotBeNull();
        r.Coverage!.UnknownLevers.Should().BeGreaterThanOrEqualTo(1); // ReBAR not read → coverage < full
        r.Coverage.KnownLevers.Should().BeLessThan(r.Coverage.ApplicableLevers);
    }

    [Fact]
    public void Clean_machine_has_full_coverage_and_no_unknown()
    {
        var r = new ScanScorer().Score(Hw(), State(), null).Value!;
        r.Coverage.Should().NotBeNull();
        r.Coverage!.UnknownLevers.Should().Be(0);
        r.Coverage.KnownLevers.Should().Be(r.Coverage.ApplicableLevers);
    }

    // --- ledger : every deducted point is attributable to a named lever. -------------------------

    [Fact]
    public void Ledger_total_equals_one_hundred_minus_score()
    {
        var hw = Hw(driver: "9.1"); // stale driver −15
        // XMP −15. Balanced + coeurs parques ne coute plus rien sans mesure (lot 1, 10/09/2026) :
        // le parking est identique entre les plans, ce n'etait pas un probleme prouve.
        var st = State(xmp: TriState.Off, power: Balanced, parked: new[] { 1 });
        var r = new ScanScorer().Score(hw, st, null).Value!;

        r.Ledger.Should().NotBeNull();
        var totalPenalty = r.Ledger!.Sum(e => e.Penalty);
        (100 - totalPenalty).Should().Be(r.Score);
        r.Ledger.Should().Contain(e => e.LeverId == "xmp" && e.State == LeverState.Suboptimal && e.Penalty == 15);
        r.Ledger.Should().Contain(e => e.LeverId == "gpu-driver" && e.Penalty == 15);
        r.Ledger.Should().NotContain(e => e.LeverId == "power-plan");
    }

    [Fact]
    public void Latency_dimension_status_is_pending_when_no_baseline()
    {
        var r = new ScanScorer().Score(Hw(), State(), baseline: null).Value!;
        r.DimensionStatus.LatencyMeasured.Should().BeFalse();
    }

    [Fact]
    public void Latency_dimension_status_measured_when_baseline_has_latency()
    {
        var baselineWithLatency = new BenchmarkResult(120, 90, 70, new List<double> { 8.0 }, 45.0, 1.0, 5);
        var r = new ScanScorer().Score(Hw(), State(), baselineWithLatency).Value!;
        r.DimensionStatus.LatencyMeasured.Should().BeTrue();
    }

    [Fact]
    public void Stability_is_measured_when_a_baseline_exists_else_flagged_not_assessed()
    {
        // NS-6 §1: with a PresentMon baseline the Stability dimension is MEASURED (smooth capture →
        // fully assessed); with no baseline it stays a config estimate flagged not-fully-assessed.
        new ScanScorer().Score(Hw(), State(), Baseline).Value!
            .DimensionStatus.StabilityFullyAssessed.Should().BeTrue();
        new ScanScorer().Score(Hw(), State(), baseline: null).Value!
            .DimensionStatus.StabilityFullyAssessed.Should().BeFalse();
    }

    [Fact]
    public void Stability_sub_score_is_100_for_a_clean_machine_with_a_smooth_capture()
    {
        // Clean config (no stability penalty) + a smooth measured baseline → 100, now fully assessed
        // by measurement (the old "always limited in M1" humility no longer applies once we measure).
        var r = new ScanScorer().Score(Hw(), State(), Baseline).Value!;
        r.Breakdown.Stability.Should().Be(100);
        r.DimensionStatus.StabilityFullyAssessed.Should().BeTrue();
    }

    // --- NS-6 (3) : Stability becomes MEASURED when a PresentMon baseline exists. ----------------

    // Nine smooth 10 ms frames + one 90 ms spike → 50 % of time stuttering (CapFrameX 2.5× factor).
    private static BenchmarkResult BaselineWithStutter =>
        new(120, 90, 70, new List<double> { 10, 10, 10, 10, 10, 10, 10, 10, 10, 90 }, null, 1.0, 5);

    [Fact]
    public void Measured_stutter_lowers_the_stability_sub_score_on_a_clean_config()
    {
        // Config has no stability penalty, but the measured capture shows 50 % stutter time →
        // Stability dimension = 100 − 50 = 50, and it is now fully assessed.
        var r = new ScanScorer().Score(Hw(), State(), BaselineWithStutter).Value!;
        r.Breakdown.Stability.Should().Be(50);
        r.DimensionStatus.StabilityFullyAssessed.Should().BeTrue();
    }

    [Fact]
    public void Measured_stability_never_hides_a_real_config_penalty()
    {
        // Pagefile disabled (config stability 95) + a perfectly smooth capture (measured 100):
        // min() keeps the worse → 95. A smooth capture must never paper over a real misconfig.
        var r = new ScanScorer().Score(Hw(), State(pagefile: TriState.Off), Baseline).Value!;
        r.Breakdown.Stability.Should().Be(95);
        r.DimensionStatus.StabilityFullyAssessed.Should().BeTrue();
    }

    [Fact]
    public void Xmp_off_lowers_performance_sub_score()
    {
        var rOn = new ScanScorer().Score(Hw(), State(xmp: TriState.On), null).Value!;
        var rOff = new ScanScorer().Score(Hw(), State(xmp: TriState.Off), null).Value!;
        rOff.Breakdown.Performance.Should().BeLessThan(rOn.Breakdown.Performance);
    }

    [Fact]
    public void ScanResult_carries_breakdown_and_dimension_status()
    {
        var r = new ScanScorer().Score(Hw(), State(), Baseline).Value!;
        r.Breakdown.Should().NotBeNull();
        r.DimensionStatus.Should().NotBeNull();
    }
}
