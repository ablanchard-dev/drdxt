using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Foundation tests for <see cref="BiosAdvisor"/>: detect→diff over M1's
/// HardwareInventory + SettingsState (it never re-detects, never writes).
/// Honesty is the spine: a setting we cannot read = an honest "not read" Finding,
/// never a fabricated state; a well-tuned machine = an empty list (valid result).
/// Every Finding carries its decision-doc §2 matrix class.
/// </summary>
public class BiosAdvisorTests
{
    private static readonly IBiosAdvisor Advisor = new BiosAdvisor();

    // ── Builders: a deliberately mis-tuned desktop baseline we mutate per test. ──

    private static HardwareInventory Hw(
        bool isX3D = false,
        int ccdCount = 1,
        string gpuVendor = "NVIDIA",
        TriState gpuReBar = TriState.Off,
        ChassisType chassis = ChassisType.Desktop,
        bool isUefi = true,
        int configuredMhz = 4800,
        int ratedMhz = 6000,
        DateTimeOffset? biosDate = null)
        => new(
            Cpu: new CpuInfo("Test CPU", "GenuineIntel", 8, 16, isX3D, ccdCount),
            Gpus: new[] { new GpuInfo("Test GPU", gpuVendor, 8L * 1024 * 1024 * 1024, "999.99", gpuReBar) },
            Memory: new[] { new MemoryModule("PN", "Mfr", configuredMhz, ratedMhz) },
            Board: new MotherboardInfo("ASUSTeK", "ROG STRIX B650-A"),
            Bios: new BiosInfo("1.0", biosDate ?? DateTimeOffset.UtcNow, isUefi),
            Chassis: chassis);

    private static SettingsState State(
        TriState xmp = TriState.Off,
        TriState reBar = TriState.Off)
        => new(
            XmpExpo: xmp,
            ReBar: reBar,
            PowerPlanGuid: "381b4222-f694-41f0-9685-ff5bb260df2e",
            Hags: TriState.On,
            GameMode: TriState.On,
            ParkedCoreIndexes: Array.Empty<int>(),
            Vrr: TriState.On);

    private static IReadOnlyList<BiosFinding> Advise(HardwareInventory hw, SettingsState state)
    {
        var r = Advisor.Advise(hw, state);
        r.IsOk.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    // ════════════════ XMP / EXPO ════════════════

    [Fact] // A1
    public void XmpOff_emits_Major_Guide_Enable_finding()
    {
        var f = Advise(Hw(), State(xmp: TriState.Off))
            .Single(x => x.Kind == FirmwareSettingKind.XmpExpo);

        f.Severity.Should().Be(Severity.Major);
        f.MatrixClass.Should().Be(BiosMatrixClass.Guide);
        f.CurrentState.Should().Be(TriState.Off);
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Enable);
        f.StateNotReadable.Should().BeFalse();
        // 23/09/2026 : plus de fourchette (voir No_bios_gain_shows_a_range_nothing_public_holds).
        f.Gain.Should().NotBeNull();
        f.Gain!.MinPct.Should().BeNull();
        f.Gain.SourceKey.Should().NotBeNullOrWhiteSpace();
        f.Gain.CaveatKey.Should().NotBeNullOrWhiteSpace();
        f.TitleKey.Should().NotBeNullOrWhiteSpace();
        f.RationaleKey.Should().NotBeNullOrWhiteSpace();
        f.RecommendedActionKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact] // A2 — honesty: already ON → no XMP finding (no fabricated need)
    public void XmpOn_emits_no_XmpExpo_finding()
    {
        Advise(Hw(), State(xmp: TriState.On))
            .Should().NotContain(x => x.Kind == FirmwareSettingKind.XmpExpo);
    }

    [Fact] // A3 — Unknown → honest Info "not read", never asserts a state
    public void XmpUnknown_emits_Info_Read_NotReadable_finding()
    {
        var f = Advise(Hw(), State(xmp: TriState.Unknown))
            .Single(x => x.Kind == FirmwareSettingKind.XmpExpo);

        f.Severity.Should().Be(Severity.Info);
        f.MatrixClass.Should().Be(BiosMatrixClass.Read);
        f.CurrentState.Should().Be(TriState.Unknown);
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Verify);
        f.StateNotReadable.Should().BeTrue();
        f.Gain.Should().BeNull();
    }

    // ════════════════ ReBAR + ordered prerequisites ════════════════

    [Fact] // A4 — ReBar off (UEFI) → ordered prereqs Above-4G → CSM → ReBAR
    public void ReBarOff_uefi_emits_ordered_prerequisites()
    {
        var findings = Advise(Hw(gpuReBar: TriState.Off, isUefi: true), State(reBar: TriState.Off));

        var kinds = findings.Select(f => f.Kind).ToList();
        kinds.Should().Contain(FirmwareSettingKind.Above4G);
        kinds.Should().Contain(FirmwareSettingKind.Csm);
        kinds.Should().Contain(FirmwareSettingKind.ReBar);

        // Exact ordering of the chain: Above-4G ON → CSM OFF → Re-Size BAR ON.
        kinds.IndexOf(FirmwareSettingKind.Above4G)
            .Should().BeLessThan(kinds.IndexOf(FirmwareSettingKind.Csm));
        kinds.IndexOf(FirmwareSettingKind.Csm)
            .Should().BeLessThan(kinds.IndexOf(FirmwareSettingKind.ReBar));

        findings.Single(f => f.Kind == FirmwareSettingKind.Above4G)
            .RecommendedAction.Should().Be(BiosRecommendedAction.Enable);
        findings.Single(f => f.Kind == FirmwareSettingKind.Csm)
            .RecommendedAction.Should().Be(BiosRecommendedAction.Disable);
        findings.Single(f => f.Kind == FirmwareSettingKind.ReBar)
            .RecommendedAction.Should().Be(BiosRecommendedAction.Enable);

        // every prereq carries a matrix class (V1 = Guide)
        findings.Where(f => f.Kind is FirmwareSettingKind.Above4G
                                 or FirmwareSettingKind.Csm
                                 or FirmwareSettingKind.ReBar)
            .Should().OnlyContain(f => f.MatrixClass == BiosMatrixClass.Guide);
    }

    [Fact] // A5 — ReBar already ON → no ReBar/Above4G/CSM findings
    public void ReBarOn_emits_no_rebar_chain()
    {
        var findings = Advise(Hw(gpuReBar: TriState.On), State(reBar: TriState.On));
        findings.Select(f => f.Kind).Should()
            .NotContain(new[] { FirmwareSettingKind.ReBar, FirmwareSettingKind.Above4G, FirmwareSettingKind.Csm });
    }

    [Fact] // A6 — ReBar Unknown on NVIDIA → recommend-with-caveat, Info, never Major/force
    public void ReBarUnknown_nvidia_emits_Info_caveat_finding()
    {
        var f = Advise(Hw(gpuVendor: "NVIDIA", gpuReBar: TriState.Unknown), State(reBar: TriState.Unknown))
            .Single(x => x.Kind == FirmwareSettingKind.ReBar);

        f.Severity.Should().Be(Severity.Info);
        f.MatrixClass.Should().Be(BiosMatrixClass.Read);
        f.CurrentState.Should().Be(TriState.Unknown);
        f.StateNotReadable.Should().BeTrue();
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Verify);
        // Unknown must not drag in CSM/Above-4G write steps.
        Advise(Hw(gpuVendor: "NVIDIA", gpuReBar: TriState.Unknown), State(reBar: TriState.Unknown))
            .Select(x => x.Kind).Should()
            .NotContain(new[] { FirmwareSettingKind.Above4G, FirmwareSettingKind.Csm });
    }

    [Fact] // A7 — ReBar off, known (AMD) → normal Guide finding, no "unknown" caveat
    public void ReBarOff_amd_emits_normal_finding_not_caveat()
    {
        var f = Advise(Hw(gpuVendor: "AMD", gpuReBar: TriState.Off), State(reBar: TriState.Off))
            .Single(x => x.Kind == FirmwareSettingKind.ReBar);

        f.MatrixClass.Should().Be(BiosMatrixClass.Guide);
        f.StateNotReadable.Should().BeFalse();
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Enable);
    }

    // ════════════════ Boot mode / CSM guard (no-boot safety) ════════════════

    [Fact] // B2 — Legacy boot + ReBar off → do not advise CSM-off (no-boot risk)
    public void Legacy_boot_suppresses_csm_off_recommendation()
    {
        var findings = Advise(Hw(gpuReBar: TriState.Off, isUefi: false), State(reBar: TriState.Off));

        findings.Should().NotContain(f => f.Kind == FirmwareSettingKind.Csm,
            "advising CSM-off on a Legacy/MBR system risks no-boot");
        // The ReBar finding is still surfaced, just without the dangerous CSM step.
        findings.Should().Contain(f => f.Kind == FirmwareSettingKind.ReBar);
    }

    // ════════════════ X3D / CPPC ════════════════

    [Fact] // A8 — multi-CCD X3D → a Verify finding (Windows-side first), not a Major BIOS write
    public void MultiCcd_X3D_emits_verify_finding_not_a_bios_write()
    {
        var f = Advise(Hw(isX3D: true, ccdCount: 2), State())
            .Single(x => x.Kind == FirmwareSettingKind.X3dCppc);

        // Softened (decision §2: primary fix is Windows-side, BIOS CPPC only "if mis-set").
        f.Severity.Should().Be(Severity.Minor);
        f.MatrixClass.Should().Be(BiosMatrixClass.Read, "the primary routing fix is Windows-side, not a BIOS step");
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Verify);
        f.StateNotReadable.Should().BeTrue();
    }

    [Fact] // A8 — non-X3D or single CCD → no X3D finding
    public void NonX3D_emits_no_X3dCppc_finding()
    {
        Advise(Hw(isX3D: false, ccdCount: 1), State())
            .Should().NotContain(x => x.Kind == FirmwareSettingKind.X3dCppc);
        Advise(Hw(isX3D: true, ccdCount: 1), State())
            .Should().NotContain(x => x.Kind == FirmwareSettingKind.X3dCppc);
    }

    // ════════════════ PCIe link — honest "not read" ════════════════

    [Fact] // PCIe link info is not in M1 inventory → honest not-read finding, never fabricated
    public void PcieGen_emits_honest_not_read_finding()
    {
        var f = Advise(Hw(), State())
            .Single(x => x.Kind == FirmwareSettingKind.PcieGen);

        f.Severity.Should().Be(Severity.Info);
        f.MatrixClass.Should().Be(BiosMatrixClass.Read);
        f.CurrentState.Should().Be(TriState.Unknown);
        f.StateNotReadable.Should().BeTrue();
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Verify);
        f.Gain.Should().BeNull();
    }

    // ════════════════ BIOS version / AGESA ════════════════

    [Fact] // old BIOS date → informational update suggestion (no KB = no hard claim)
    public void OldBios_emits_Info_update_suggestion()
    {
        var old = DateTimeOffset.UtcNow.AddYears(-4);
        var f = Advise(Hw(biosDate: old), State())
            .Single(x => x.Kind == FirmwareSettingKind.BiosVersion);

        f.Severity.Should().Be(Severity.Info);
        f.MatrixClass.Should().Be(BiosMatrixClass.Read);
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Update);
    }

    [Fact] // recent BIOS → no version finding
    public void RecentBios_emits_no_version_finding()
    {
        Advise(Hw(biosDate: DateTimeOffset.UtcNow.AddMonths(-3)), State())
            .Should().NotContain(x => x.Kind == FirmwareSettingKind.BiosVersion);
    }

    // ════════════════ Chassis gating (§16) ════════════════

    [Theory] // A9 — laptop/handheld BIOS is locked → no actionable firmware findings
    [InlineData(ChassisType.Laptop)]
    [InlineData(ChassisType.Handheld)]
    public void LockedChassis_emits_no_actionable_findings(ChassisType chassis)
    {
        var findings = Advise(Hw(chassis: chassis, gpuReBar: TriState.Off, isX3D: true, ccdCount: 2),
                              State(xmp: TriState.Off, reBar: TriState.Off));

        // No Guide/Forbidden write recommendations on a locked machine.
        findings.Should().NotContain(f => f.MatrixClass == BiosMatrixClass.Guide);
        findings.Select(f => f.Kind).Should().NotContain(new[]
        {
            FirmwareSettingKind.XmpExpo, FirmwareSettingKind.ReBar,
            FirmwareSettingKind.Above4G, FirmwareSettingKind.Csm,
            FirmwareSettingKind.X3dCppc, FirmwareSettingKind.PcieGen,
        });
        // A single honest informational finding explaining non-applicability.
        findings.Should().ContainSingle()
            .Which.MatrixClass.Should().Be(BiosMatrixClass.Read);
        findings.Single().Severity.Should().Be(Severity.Info);
    }

    // ════════════════ Defensive boundary ════════════════

    [Fact] // A10 — empty memory while inferring XMP → clean Result.Fail, no exception
    public void EmptyMemory_fails_clean()
    {
        var hw = Hw() with { Memory = Array.Empty<MemoryModule>() };
        var r = Advisor.Advise(hw, State());

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosInputInvalid);
    }

    [Fact] // A10 — no GPU while diffing ReBar → clean Result.Fail
    public void NoGpu_fails_clean()
    {
        var hw = Hw() with { Gpus = Array.Empty<GpuInfo>() };
        var r = Advisor.Advise(hw, State());

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosInputInvalid);
    }

    // ════════════════ Integration: every finding is honest + classed ════════════════

    [Fact] // A11 — fully mis-tuned desktop → exactly the expected actionable set
    public void MisTunedDesktop_emits_expected_set()
    {
        var findings = Advise(
            Hw(isX3D: true, ccdCount: 2, gpuVendor: "AMD", gpuReBar: TriState.Off, isUefi: true),
            State(xmp: TriState.Off, reBar: TriState.Off));

        findings.Select(f => f.Kind).Should().Contain(new[]
        {
            FirmwareSettingKind.XmpExpo,
            FirmwareSettingKind.Above4G,
            FirmwareSettingKind.Csm,
            FirmwareSettingKind.ReBar,
            FirmwareSettingKind.X3dCppc,
        });
    }

    [Fact] // A11 — well-tuned desktop → "nothing to tune in BIOS" (only honest not-read infos)
    public void WellTunedDesktop_has_no_actionable_findings()
    {
        var findings = Advise(
            Hw(isX3D: false, ccdCount: 1, gpuVendor: "NVIDIA", gpuReBar: TriState.On,
               isUefi: true, configuredMhz: 6000, ratedMhz: 6000,
               biosDate: DateTimeOffset.UtcNow.AddMonths(-2)),
            State(xmp: TriState.On, reBar: TriState.On));

        // No actionable (Guide) finding — a valid, assumed "already good" result.
        findings.Should().NotContain(f => f.MatrixClass == BiosMatrixClass.Guide);
        // Any remaining finding is purely informational/honest-not-read.
        findings.Should().OnlyContain(f =>
            f.Severity == Severity.Info && f.MatrixClass == BiosMatrixClass.Read);
    }

    [Fact] // global invariant: every finding carries a non-default matrix class + resx keys
    public void EveryFinding_carries_matrix_class_and_keys()
    {
        var findings = Advise(
            Hw(isX3D: true, ccdCount: 2, gpuVendor: "NVIDIA", gpuReBar: TriState.Off),
            State(xmp: TriState.Off, reBar: TriState.Off));

        findings.Should().NotBeEmpty();
        findings.Should().OnlyContain(f =>
            !string.IsNullOrWhiteSpace(f.TitleKey) &&
            !string.IsNullOrWhiteSpace(f.RationaleKey) &&
            !string.IsNullOrWhiteSpace(f.RecommendedActionKey));
        // No Finding may ever claim a write-class the foundation can't honour.
        findings.Should().OnlyContain(f =>
            f.MatrixClass == BiosMatrixClass.Guide || f.MatrixClass == BiosMatrixClass.Read);
    }

    // ════════════════ Honest gain framing (ranges/caveats, not bare %) ════════════════

    [Fact] // ReBAR is game-dependent (can regress) → conditional, NO fabricated point-%
    public void ReBar_gain_is_conditional_with_no_fabricated_percent()
    {
        var f = Advise(Hw(gpuVendor: "AMD", gpuReBar: TriState.Off, isUefi: true), State(reBar: TriState.Off))
            .Single(x => x.Kind == FirmwareSettingKind.ReBar);

        f.Gain.Should().NotBeNull();
        f.Gain!.Conditional.Should().BeTrue("ReBAR is profiled per game, not a global win");
        f.Gain.MinPct.Should().BeNull("no honest global % exists for ReBAR");
        f.Gain.MaxPct.Should().BeNull();
        f.Gain.SourceKey.Should().NotBeNullOrWhiteSpace();
        f.Gain.CaveatKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact] // X3D : only IF mis-routed → conditional, and no range on screen
    public void X3d_gain_is_conditional_with_no_range()
    {
        var f = Advise(Hw(isX3D: true, ccdCount: 2), State())
            .Single(x => x.Kind == FirmwareSettingKind.X3dCppc);

        f.Gain.Should().NotBeNull();
        f.Gain!.Conditional.Should().BeTrue("the gain is realized only if the CCD routing is mis-set");
        f.Gain.MinPct.Should().BeNull();
        f.Gain.MaxPct.Should().BeNull();
        f.Gain.SourceKey.Should().NotBeNullOrWhiteSpace();
        f.Gain.CaveatKey.Should().NotBeNullOrWhiteSpace();
    }

    // 23/09/2026 — une fourchette que rien de public ne tient ne s'affiche pas.
    //
    // L'ecran disait « XMP : ≈ 11–25 % » et « X3D : ≈ 0–10 % », source : nos notes de decision. Les
    // mesures publiques disent autre chose : pour XMP/EXPO, de quelques % dans un jeu limite par la
    // carte graphique a ~42 % dans un jeu tres sensible a la memoire (TechSpot, XDA), et ~1 % sur un
    // X3D (AMD). Une fourchette unique ne decrit la machine de personne ; la notre, recherche
    // la note memoire du projet (non publiee) le dit elle-meme : « non chiffrable de facon canonique : on mesure ». Le chiffre
    // de la personne vient de l'A/B du produit, pas d'une plage imprimee d'avance.
    [Fact]
    public void No_bios_gain_shows_a_range_nothing_public_holds()
    {
        var tout = Advise(Hw(isX3D: true, ccdCount: 2, gpuVendor: "AMD", gpuReBar: TriState.Off, isUefi: true),
                          State(xmp: TriState.Off, reBar: TriState.Off));

        tout.Where(f => f.Gain is not null).Should().HaveCountGreaterThanOrEqualTo(3, "temoin : XMP, ReBAR, X3D");
        tout.Where(f => f.Gain is { MinPct: not null } or { MaxPct: not null })
            .Select(f => f.Kind).Should().BeEmpty();
    }
}
