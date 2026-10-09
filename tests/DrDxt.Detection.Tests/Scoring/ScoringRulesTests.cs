using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Scoring;

public class ScoringRulesTests
{
    private const string BalancedPlan = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerfPlan = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    // NOTE driver version "24.10.1" → branch 24 ≥ floor 20 → FRESH (so the new GPU-driver lever
    // does not spuriously fire in tests that aren't about it). A stale fixture lives in its own test.
    private static HardwareInventory Desktop(bool x3d = false, int ccd = 1, TriState rebar = TriState.Off,
        string driver = "24.10.1", string vendor = "AMD") => new(
        new CpuInfo("CPU", "AMD", 8, 16, x3d, ccd),
        new[] { new GpuInfo("GPU", vendor, 16L<<30, driver, rebar) },
        // Deux barrettes correctement reparties (les emplacements que la machine de
        // reference rapporte reellement) : ce fixture teste XMP, pas la topologie.
        new[]
        {
            new MemoryModule("PN", "Corsair", 6000, 6000, DeviceLocator: "Controller0-DIMMA2"),
            new MemoryModule("PN", "Corsair", 6000, 6000, DeviceLocator: "Controller1-DIMMB2"),
        },
        new MotherboardInfo("MSI", "B650"), new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState State(TriState xmp = TriState.On, TriState rebar = TriState.On,
        string power = BalancedPlan, TriState hags = TriState.On, TriState gameMode = TriState.On,
        int[]? parked = null, TriState vrr = TriState.Unknown, TriState vrrCapable = TriState.Unknown) =>
        new(xmp, rebar, power, hags, gameMode, parked ?? Array.Empty<int>(), vrr, vrrCapable);

    [Fact]
    public void Xmp_off_produces_memory_finding_with_gain()
    {
        var f = ScoringRules.Evaluate(Desktop(), State(xmp: TriState.Off)).ToList();
        var mem = f.Should().ContainSingle(x => x.Category == "Memory").Subject;
        mem.Severity.Should().Be(Severity.Major);
        mem.EstimatedGainPct.Should().Be(15);    // == the penalty the score removes (audit C1 coupling)
        mem.TitleKey.Should().Be("Finding.Memory.XmpOff");
    }

    [Fact]
    public void Xmp_on_produces_no_memory_finding()
    {
        ScoringRules.Evaluate(Desktop(), State(xmp: TriState.On))
            .Should().NotContain(x => x.Category == "Memory");
    }

    // --- NS-4 : a DISABLED page file is an honest reliability finding (unscored, not "+FPS"). -----

    [Fact]
    public void Pagefile_disabled_produces_minor_reliability_finding_no_fps_claim()
    {
        var f = ScoringRules.Evaluate(Desktop(), State() with { PagefileConfigured = TriState.Off });
        var pf = f.Should().ContainSingle(x => x.TitleKey == "Finding.Memory.PagefileDisabled").Subject;
        pf.Category.Should().Be("Memory");
        pf.Severity.Should().Be(Severity.Minor); // reliability advisory, not a Major perf deficit
        pf.EstimatedGainPct.Should().Be(0);      // honest: reliability, never a claimed FPS gain
    }

    [Fact]
    public void Pagefile_present_or_unknown_produces_no_pagefile_finding()
    {
        ScoringRules.Evaluate(Desktop(), State() with { PagefileConfigured = TriState.On })
            .Should().NotContain(x => x.TitleKey == "Finding.Memory.PagefileDisabled");
        ScoringRules.Evaluate(Desktop(), State() with { PagefileConfigured = TriState.Unknown })
            .Should().NotContain(x => x.TitleKey == "Finding.Memory.PagefileDisabled");
    }

    // --- L8 suite (10/09/2026) : un ecran que le pilote dit NON capable n'appelle aucun conseil VRR. --

    [Fact]
    public void Vrr_off_sur_un_ecran_LU_non_capable_n_est_pas_une_opportunite()
        => ScoringRules.Evaluate(Desktop(), State(vrr: TriState.Off, vrrCapable: TriState.Off))
            .Should().NotContain(x => x.TitleKey == "Finding.Latency.VrrOff",
                "conseiller d'activer le VRR sur un ecran qui ne sait pas en faire est un faux besoin");

    // --- Lot 1 (10/09/2026) : le plan d'alimentation ne produit plus aucun constat. -------------
    // Le constat « Equilibre + coeurs parques » etait un faux probleme prouve : le parking est
    // identique entre les plans. La piste CPU vit dans le candidat `fixit.power_plan`, arme par la mesure.

    [Theory]
    [InlineData(BalancedPlan)]
    [InlineData(HighPerfPlan)]
    public void Aucun_plan_d_alimentation_ne_produit_de_constat(string plan)
        => ScoringRules.Evaluate(Desktop(), State(power: plan, parked: new[] { 1, 2 }))
            .Should().NotContain(x => x.TitleKey.Contains("PowerPlan"));

    // --- M12 : X3D is penalized only on a confirmed misconfig (parking symptom), else Unknown. ----

    [Fact]
    public void X3d_dual_ccd_with_symptom_produces_major_scheduling_finding()
    {
        var f = ScoringRules.Evaluate(Desktop(x3d: true, ccd: 2), State(power: HighPerfPlan, parked: new[] { 0 }));
        var x = f.Should().ContainSingle(x => x.TitleKey == "Finding.Scheduling.X3dCcd").Subject;
        x.Severity.Should().Be(Severity.Major);
        x.EstimatedGainPct.Should().Be(12);
    }

    [Fact]
    public void X3d_dual_ccd_without_symptom_is_unknown_info_not_a_deficit()
    {
        // audit M12 : routing not confirmable (parked-core reader stubbed) → Unknown info, 0 gain.
        var f = ScoringRules.Evaluate(Desktop(x3d: true, ccd: 2), State(power: HighPerfPlan));
        f.Should().NotContain(x => x.TitleKey == "Finding.Scheduling.X3dCcd");
        var info = f.Should().ContainSingle(x => x.TitleKey == "Finding.Scheduling.X3dUnknown").Subject;
        info.Severity.Should().Be(Severity.Info);
        info.EstimatedGainPct.Should().BeNull();
    }

    // AMD-TOUT (09/10/2026) : seul le CCD de fréquence parqué, c'est le routage sain (la règle du levier fixit.x3d_ccd).
    // Le docteur le disait « à vérifier », gain 12 %, sur un 7950X3D qui marchait, pendant que le levier ne proposait rien.
    [Fact]
    public void X3d_dual_ccd_with_only_frequency_ccd_parked_is_healthy()
    {
        var f = ScoringRules.Evaluate(Desktop(x3d: true, ccd: 2), State(power: HighPerfPlan, parked: new[] { 4, 5, 6, 7 }));
        f.Should().NotContain(x => x.TitleKey == "Finding.Scheduling.X3dCcd");
        f.Should().NotContain(x => x.TitleKey == "Finding.Scheduling.X3dUnknown");
    }

    [Fact]
    public void Single_ccd_x3d_no_ccd_finding()
    {
        var f = ScoringRules.Evaluate(Desktop(x3d: true, ccd: 1), State(parked: new[] { 0 }));
        f.Should().NotContain(x => x.TitleKey == "Finding.Scheduling.X3dCcd");
        f.Should().NotContain(x => x.TitleKey == "Finding.Scheduling.X3dUnknown");
    }

    // --- C2 : HAGS off is a NEUTRAL opportunity (Info), never a scored deficit. ------------------

    [Fact]
    public void Hags_off_produces_neutral_info_latency_finding()
    {
        var f = ScoringRules.Evaluate(Desktop(), State(hags: TriState.Off));
        var hags = f.Should().ContainSingle(x => x.TitleKey == "Finding.Latency.HagsOff").Subject;
        hags.Severity.Should().Be(Severity.Info);       // neutral opportunity, not a Minor deficit
        hags.EstimatedGainPct.Should().BeNull();        // no gain claimed on a "non garanti" lever
    }

    // --- GPU driver freshness lever (was invisible to the score). -------------------------------

    [Fact]
    public void Stale_gpu_driver_produces_major_gpu_finding()
    {
        // AMD branch 9 < floor 20 → genuinely old driver.
        var f = ScoringRules.Evaluate(Desktop(driver: "9.1"), State());
        var drv = f.Should().ContainSingle(x => x.TitleKey == "Finding.Gpu.DriverStale").Subject;
        drv.Severity.Should().Be(Severity.Major);
        drv.EstimatedGainPct.Should().Be(15);
    }

    [Fact]
    public void Fresh_gpu_driver_produces_no_driver_finding()
    {
        ScoringRules.Evaluate(Desktop(driver: "24.10.1"), State())
            .Should().NotContain(x => x.TitleKey == "Finding.Gpu.DriverStale");
    }

    [Fact]
    public void Rebar_off_on_amd_produces_minor_finding()
    {
        var f = ScoringRules.Evaluate(Desktop(rebar: TriState.Off), State(rebar: TriState.Off));
        var reb = f.Should().ContainSingle(x => x.TitleKey == "Finding.Gpu.ReBarOff").Subject;
        reb.Severity.Should().Be(Severity.Minor); // ReBar = par-jeu, peut régresser → minor honnête
        reb.EstimatedGainPct.Should().Be(3);
    }

    [Fact]
    public void Rebar_unknown_amd_produces_info_not_a_problem_claim()
    {
        // §16 : ReBar Unknown (non-NVIDIA) → on informe, on n'affirme pas un défaut.
        var f = ScoringRules.Evaluate(Desktop(rebar: TriState.Unknown, vendor: "AMD"), State(rebar: TriState.Unknown));
        var reb = f.Should().ContainSingle(x => x.TitleKey == "Finding.Gpu.ReBarUnknown").Subject;
        reb.Severity.Should().Be(Severity.Info);
        reb.EstimatedGainPct.Should().BeNull(); // pas de gain chiffré sur de l'inconnu
    }

    [Fact]
    public void Rebar_unknown_nvidia_pushes_systematic_guidance_finding()
    {
        // audit H3 : when NVIDIA ReBAR can't be read (NVML unavailable) → guidance fallback so the
        // dominant lever is never invisible, without touching the /100 (Info, no gain).
        var f = ScoringRules.Evaluate(Desktop(rebar: TriState.Unknown, vendor: "NVIDIA", driver: "560.94"), State(rebar: TriState.Unknown));
        var reb = f.Should().ContainSingle(x => x.TitleKey == "Finding.Gpu.ReBarGuidance").Subject;
        reb.Severity.Should().Be(Severity.Info);
        reb.EstimatedGainPct.Should().BeNull();
    }

    [Fact]
    public void Laptop_does_not_produce_xmp_finding_gating()
    {
        // §16 : laptop BIOS verrouillé → pas de tweak XMP impossible.
        var laptop = Desktop() with { Chassis = ChassisType.Laptop };
        ScoringRules.Evaluate(laptop, State(xmp: TriState.Off))
            .Should().NotContain(x => x.Category == "Memory");
    }

    [Fact]
    public void Handheld_gates_memory_and_rebar_findings()
    {
        var hh = Desktop() with { Chassis = ChassisType.Handheld };
        var f = ScoringRules.Evaluate(hh, State(xmp: TriState.Off, rebar: TriState.Off));
        f.Should().NotContain(x => x.Category == "Memory");
        f.Should().NotContain(x => x.TitleKey == "Finding.Gpu.ReBarOff");
    }

    [Fact]
    public void Findings_never_contain_fix_steps_only_categories()
    {
        // Garde-fou funnel : aucune clé de finding ne contient ".Fix." / ".Step."
        var f = ScoringRules.Evaluate(Desktop(driver: "9.1", rebar: TriState.Off),
            State(xmp: TriState.Off, hags: TriState.Off, power: BalancedPlan, parked: new[] { 1 },
                vrr: TriState.Off, vrrCapable: TriState.On));
        f.Should().OnlyContain(x => !x.TitleKey.Contains(".Fix.") && !x.TitleKey.Contains(".Step."));
    }

    // --- H2 : VRR off penalizes only when a VRR-capable display is confirmed. --------------------

    [Fact]
    public void Vrr_off_with_capable_display_produces_minor_latency_finding()
    {
        var f = ScoringRules.Evaluate(Desktop(), State(vrr: TriState.Off, vrrCapable: TriState.On));
        var vrr = f.Should().ContainSingle(x => x.TitleKey == "Finding.Latency.VrrOff").Subject;
        vrr.Severity.Should().Be(Severity.Minor);
        vrr.EstimatedGainPct.Should().Be(5);
    }

    [Fact]
    public void Vrr_off_without_capable_display_is_neutral_opportunity()
    {
        // audit H2 : no confirmed VRR display → cannot assert the gain → opportunity, 0 gain.
        // 16/09/2026 : sur la machine de reference le pilote AUTORISE G-Sync (plein ecran) mais il est
        // inactif sur les deux ecrans, capacite non lue. « VRR desactive » laissait croire a un
        // reglage a basculer ; le fait lu est « inactif, support inconnu ».
        var f = ScoringRules.Evaluate(Desktop(), State(vrr: TriState.Off, vrrCapable: TriState.Unknown));
        f.Should().NotContain(x => x.TitleKey == "Finding.Latency.VrrOff");
        var vrr = f.Should().ContainSingle(x => x.TitleKey == "Finding.Latency.VrrInactiveSupportUnknown").Subject;
        vrr.Severity.Should().Be(Severity.Info);
        vrr.EstimatedGainPct.Should().BeNull();
    }

    [Fact]
    public void Vrr_unknown_produces_info_finding_no_gain_claimed()
    {
        var f = ScoringRules.Evaluate(Desktop(), State(vrr: TriState.Unknown));
        var vrr = f.Should().ContainSingle(x => x.TitleKey == "Finding.Latency.VrrUnknown").Subject;
        vrr.Severity.Should().Be(Severity.Info);
        vrr.EstimatedGainPct.Should().BeNull();
    }

    [Fact]
    public void Vrr_on_produces_no_vrr_finding()
    {
        ScoringRules.Evaluate(Desktop(), State(vrr: TriState.On, vrrCapable: TriState.On))
            .Should().NotContain(x => x.TitleKey == "Finding.Latency.VrrOff");
    }
}
