using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Scoring;

public class ScanScorerTests
{
    private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    // Pilote « 24.10.1 » → branche 24 ≥ plancher 20 → à jour (un gabarit propre note 100).
    private static HardwareInventory Hw(bool x3d = false, int ccd = 1, ChassisType chassis = ChassisType.Desktop,
        string vendor = "AMD", TriState rebar = TriState.On, string driver = "24.10.1") => new(
        new CpuInfo("CPU", "AMD", 8, 16, x3d, ccd),
        new[] { new GpuInfo("GPU", vendor, 16L<<30, driver, rebar) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B650"), new BiosInfo("1", DateTimeOffset.UtcNow, true), chassis);

    // `displayRefresh` par defaut = ecran LU et deja a son maximum. C'est ce que « machine
    // propre » veut dire depuis que le levier de frequence d'affichage existe -- un ecran dont les
    // modes n'ont pas pu etre enumeres est honnetement NON COUVERT, et le laisser a null ferait
    // porter a tous ces tests une couverture incomplete qui n'est pas leur sujet.
    private static SettingsState State(TriState xmp = TriState.On, string power = HighPerf,
        TriState hags = TriState.On, TriState gameMode = TriState.On, int[]? parked = null,
        TriState vrr = TriState.Unknown, TriState vrrCapable = TriState.Unknown, TriState rebar = TriState.On,
        RefreshHeadroom? displayRefresh = null,
        // Idem pour les degats d'un optimiseur precedent. « Machine propre » veut dire
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
        // Machine propre (XMP actif, Performances élevées, pilote à jour, ReBar actif, pas de mauvaise configuration X3D) → aucune pénalité.
        var r = new ScanScorer().Score(Hw(), State(), Baseline).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(0);
    }

    [Fact]
    public void Xmp_off_lowers_score()
    {
        var r = new ScanScorer().Score(Hw(), State(xmp: TriState.Off), Baseline).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(15); // seul le vrai levier déduit (15)
        r.Findings.Should().Contain(x => x.Category == "Memory");
    }

    [Fact]
    public void Score_is_clamped_between_0_and_100()
    {
        var bad = State(xmp: TriState.Off, power: Balanced, hags: TriState.Off);
        var r = new ScanScorer().Score(Hw(x3d: true, ccd: 2), bad, Baseline).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().BeInRange(0, 100);
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
        // « Latence : optimisations disponibles pour ta configuration » etait ajoute a
        // Toute machine, sans rien lire. Un constat qui ne vient d'aucune lecture n'a pas sa place.
        var r = new ScanScorer().Score(Hw(), State(), Baseline).Value!;
        r.Findings.Should().NotContain(x => x.TitleKey == "Finding.Latency.Available");
        r.Ledger!.Sum(e => e.Penalty).Should().Be(0);
    }

    // --- Les défauts Windows neutres ne doivent pas fabriquer un déficit. -----------------------

    [Fact]
    public void Neutral_windows_defaults_do_not_lower_the_score()
    {
        // HAGS désactivé + Équilibré (sans symptôme) + Mode Jeu désactivé + VRR désactivé sur un écran non confirmé :
        // tout est neutre. Seuls les vrais leviers filtrés déduisent → aucun ici → 100. Un modèle qui
        // punirait ces neutres ferait tomber un Windows d'origine à ~80.
        var st = State(xmp: TriState.On, power: Balanced, hags: TriState.Off, gameMode: TriState.Off,
            vrr: TriState.Off, vrrCapable: TriState.Unknown);
        var r = new ScanScorer().Score(Hw(), st, null).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(0);
    }

    [Fact]
    public void Stock_oem_only_deducts_the_real_lever_not_the_neutrals()
    {
        // XMP désactivé (le seul vrai levier) + Équilibré + HAGS désactivé + VRR inconnu + ReBar inconnu, sans X3D.
        // Seul XMP (15) déduit → 85, pas 80 (les neutres ne sont plus punis).
        var hw = Hw(vendor: "NVIDIA", rebar: TriState.Unknown, driver: "560.94");
        var st = State(xmp: TriState.Off, power: Balanced, hags: TriState.Off, gameMode: TriState.Off,
            vrr: TriState.Unknown, vrrCapable: TriState.Unknown);
        var r = new ScanScorer().Score(hw, st, null).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(15);
    }

    [Fact]
    public void Well_tuned_machine_scores_90_plus()
    {
        var r = new ScanScorer().Score(Hw(), State(vrr: TriState.On, vrrCapable: TriState.On), null).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public void Sub_scores_are_in_valid_range()
    {
        var r = new ScanScorer().Score(Hw(), State(), Baseline).Value!;
        r.Ledger!.Where(e => e.Group == "Performance").Sum(e => e.Penalty).Should().BeInRange(0, 100);
        r.Ledger!.Where(e => e.Group == "Latency").Sum(e => e.Penalty).Should().BeInRange(0, 100);
        r.Ledger!.Where(e => e.Group == "Stability").Sum(e => e.Penalty).Should().BeInRange(0, 100);
    }

    // --- Levier pilote graphique (invisible pour le score auparavant). ---------------------------

    [Fact]
    public void Stale_gpu_driver_lowers_score_by_major_magnitude()
    {
        var r = new ScanScorer().Score(Hw(driver: "9.1"), State(), null).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(15); // Major −15
        r.Ledger!.Where(e => e.Group == "Performance").Sum(e => e.Penalty).Should().Be(15);
        r.Findings.Should().Contain(x => x.TitleKey == "Finding.Gpu.DriverStale");
    }

    // --- X3D pénalisé seulement sur mauvaise configuration confirmée. -------------------

    [Fact]
    public void X3d_dual_ccd_without_parking_symptom_is_not_penalized()
    {
        var r = new ScanScorer().Score(Hw(x3d: true, ccd: 2), State(), null).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(0); // Unknown → 0, ne jamais punir une machine X3D saine
        r.Findings.Should().Contain(x => x.TitleKey == "Finding.Scheduling.X3dUnknown");
    }

    [Fact]
    public void X3d_dual_ccd_with_parking_symptom_is_penalized()
    {
        var r = new ScanScorer().Score(Hw(x3d: true, ccd: 2), State(power: HighPerf, parked: new[] { 0, 1 }), null).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(12); // Major −12
    }

    // --- Le plan d'alimentation n'est plus un levier du registre. -----------

    [Fact]
    public void Balanced_with_parked_cores_no_longer_lowers_stability()
    {
        // Le parking est identique entre Equilibre et Haute performance : ce n'etait pas un defaut.
        var r = new ScanScorer().Score(Hw(), State(power: Balanced, parked: new[] { 2 }), null).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(0);
        r.Ledger!.Where(e => e.Group == "Stability").Sum(e => e.Penalty).Should().Be(0);
    }

    [Fact]
    public void Balanced_without_symptom_does_not_lower_stability()
    {
        var r = new ScanScorer().Score(Hw(), State(power: Balanced), null).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(0);
        r.Ledger!.Where(e => e.Group == "Stability").Sum(e => e.Penalty).Should().Be(0);
    }

    // --- VRR désactivé filtré sur un écran capable de VRR. -----------------------------

    [Fact]
    public void Vrr_off_with_capable_display_lowers_latency_sub_score()
    {
        var stateVrrOn = State(vrr: TriState.On, vrrCapable: TriState.On);
        var stateVrrOff = stateVrrOn with { Vrr = TriState.Off };

        var rOn = new ScanScorer().Score(Hw(), stateVrrOn, null).Value!;
        var rOff = new ScanScorer().Score(Hw(), stateVrrOff, null).Value!;

        rOff.Findings.Should().Contain(x => x.TitleKey == "Finding.Latency.VrrOff");
        rOff.Ledger!.Where(e => e.Group == "Latency").Sum(e => e.Penalty).Should().Be(5);
    }

    [Fact]
    public void Vrr_off_without_capable_display_does_not_penalize()
    {
        // Aucun écran VRR confirmé → 0 pénalité (un écran 60 Hz sans VRR est correctement réglé).
        var r = new ScanScorer().Score(Hw(), State(vrr: TriState.Off, vrrCapable: TriState.Unknown), null).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(0);
        r.Ledger!.Where(e => e.Group == "Latency").Sum(e => e.Penalty).Should().Be(0);
        // remonte toujours comme constat d'opportunité neutre
        r.Findings.Should().Contain(x => x.TitleKey == "Finding.Latency.VrrInactiveSupportUnknown" && x.Severity == Severity.Info);
    }

    [Fact]
    public void Vrr_unknown_no_penalty_finding_is_info()
    {
        var stateVrrOn = State(vrr: TriState.On, vrrCapable: TriState.On);
        var stateVrrUnknown = stateVrrOn with { Vrr = TriState.Unknown };

        var rOn = new ScanScorer().Score(Hw(), stateVrrOn, null).Value!;
        var rUnknown = new ScanScorer().Score(Hw(), stateVrrUnknown, null).Value!;

        // Unknown ne doit pas pénaliser
        rUnknown.Ledger!.Sum(e => e.Penalty).Should().Be(rOn.Ledger!.Sum(e => e.Penalty));

        var vrrFinding = rUnknown.Findings.FirstOrDefault(x => x.TitleKey == "Finding.Latency.VrrUnknown");
        vrrFinding.Should().NotBeNull();
        vrrFinding!.Severity.Should().Be(Severity.Info);
    }

    // --- Couverture : Unknown coûte 0 mais reste visible. ------------------------------

    [Fact]
    public void Nvidia_rebar_unknown_keeps_score_100_but_pushes_guidance_and_marks_coverage()
    {
        var r = new ScanScorer().Score(Hw(vendor: "NVIDIA", rebar: TriState.Unknown, driver: "560.94"), State(), null).Value!;
        r.Ledger!.Sum(e => e.Penalty).Should().Be(0); // Unknown ne pénalise jamais
        r.Findings.Should().Contain(x => x.TitleKey == "Finding.Gpu.ReBarGuidance"); // guidance
        r.Coverage.Should().NotBeNull();
        r.Coverage!.UnknownLevers.Should().BeGreaterThanOrEqualTo(1); // ReBAR non lu → couverture incomplète
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

    // --- registre : chaque point déduit est attribuable à un levier nommé. -----------------------

    [Fact]
    public void The_ledger_names_every_lever_that_costs()
    {
        var hw = Hw(driver: "9.1"); // pilote périmé −15
        // XMP −15. Balanced + coeurs parques ne coute plus rien sans mesure :
        // le parking est identique entre les plans, ce n'etait pas un probleme prouve.
        var st = State(xmp: TriState.Off, power: Balanced, parked: new[] { 1 });
        var r = new ScanScorer().Score(hw, st, null).Value!;

        r.Ledger.Should().NotBeNull();
        r.Ledger.Should().Contain(e => e.LeverId == "xmp" && e.State == LeverState.Suboptimal && e.Penalty == 15);
        r.Ledger.Should().Contain(e => e.LeverId == "gpu-driver" && e.Penalty == 15);
        r.Ledger.Should().NotContain(e => e.LeverId == "power-plan");
    }

    [Fact]
    public void Stability_sub_score_is_100_for_a_clean_machine_with_a_smooth_capture()
    {
        // Configuration propre (aucune pénalité de stabilité) + un départ mesuré lisse → 100, désormais
        // pleinement évalué par la mesure (l'humilité « toujours limité » ne s'applique plus une fois mesuré).
        var r = new ScanScorer().Score(Hw(), State(), Baseline).Value!;
        r.Ledger!.Where(e => e.Group == "Stability").Sum(e => e.Penalty).Should().Be(0);
    }

    [Fact]
    public void Measured_stability_never_hides_a_real_config_penalty()
    {
        // Fichier d'échange désactivé (stabilité de configuration 95) + une capture parfaitement lisse (mesuré 100) :
        // min() garde le pire → 95. Une capture lisse ne doit jamais masquer une vraie mauvaise configuration.
        var r = new ScanScorer().Score(Hw(), State(pagefile: TriState.Off), Baseline).Value!;
        r.Ledger!.Where(e => e.Group == "Stability").Sum(e => e.Penalty).Should().Be(5);
    }

    [Fact]
    public void Xmp_off_lowers_performance_sub_score()
    {
        var rOn = new ScanScorer().Score(Hw(), State(xmp: TriState.On), null).Value!;
        var rOff = new ScanScorer().Score(Hw(), State(xmp: TriState.Off), null).Value!;
        rOff.Ledger!.Where(e => e.Group == "Performance").Sum(e => e.Penalty)
            .Should().BeGreaterThan(rOn.Ledger!.Where(e => e.Group == "Performance").Sum(e => e.Penalty));
    }

}
