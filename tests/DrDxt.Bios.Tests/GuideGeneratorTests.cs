using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Tests for the guide GENERATOR (content assembly, not rendering). The guide shows
/// Only what is mis-set on this machine, with exact paths (or a labelled fallback +
/// exact setting name), the ReBAR dependency order, and the mandatory recovery
/// section top+bottom. §16 guards: Legacy → no CSM-off; uncovered board → Unverified;
/// non-Guide findings → no write step. Never a write.
/// </summary>
public class GuideGeneratorTests
{
    private static IGuideGenerator Gen() => new GuideGenerator(GuideLibraryFactory.CreateDefault());

    // A desktop UEFI ASUS B650 (covered board) we mutate per test.
    private static HardwareInventory Hw(
        string manufacturer = "ASUSTeK COMPUTER INC.",
        string product = "ROG STRIX B650-A GAMING WIFI",
        bool isUefi = true,
        bool isX3D = true,
        int ccd = 2)
        => new(
            Cpu: new CpuInfo("Ryzen 7 7800X3D", "AuthenticAMD", 8, 16, isX3D, ccd),
            Gpus: new[] { new GpuInfo("RTX 4080", "NVIDIA", 16L << 30, "999.99", TriState.Off) },
            Memory: new[] { new MemoryModule("PN", "Mfr", 4800, 6000) },
            Board: new MotherboardInfo(manufacturer, product),
            Bios: new BiosInfo("2.0", DateTimeOffset.UtcNow.AddMonths(-2), isUefi),
            Chassis: ChassisType.Desktop);

    private static BiosFinding Finding(
        FirmwareSettingKind kind,
        BiosMatrixClass cls = BiosMatrixClass.Guide,
        BiosRecommendedAction action = BiosRecommendedAction.Enable)
        => new(kind, Severity.Major, "title", "rationale", cls, TriState.Off,
               action, "action", null, StateNotReadable: cls != BiosMatrixClass.Guide);

    private static PersonalizedGuide Generate(HardwareInventory hw, params BiosFinding[] findings)
    {
        var r = Gen().Generate(hw, findings);
        r.IsOk.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    // ── D1: only what is mis-set is in the guide; board label is injected. ──

    [Fact]
    public void Only_misset_settings_get_steps()
    {
        var g = Generate(Hw(), Finding(FirmwareSettingKind.XmpExpo));

        g.Steps.Should().Contain(s => s.Setting == FirmwareSettingKind.XmpExpo);
        g.Steps.Should().NotContain(s => s.Setting == FirmwareSettingKind.ReBar);
        g.Steps.Should().NotContain(s => s.Setting == FirmwareSettingKind.X3dCppc);
        g.DetectedBoardLabel.Should().Contain("ASUSTeK").And.Contain("ROG STRIX B650-A");
    }

    // ── HONESTY (decision §9.1): a recognized vendor on a board not in the board+BIOS
    //    KB gives the best-known ASUS path but is never labelled Verified. ──

    [Fact]
    public void Recognized_vendor_uncovered_board_is_not_verified_but_keeps_best_known_path()
    {
        var g = Generate(Hw(), Finding(FirmwareSettingKind.XmpExpo));

        // No board+BIOS KB entry ships → must be the honest "not confirmed" label,
        // never "Verified for your exact board".
        g.Confidence.Should().Be(GuideConfidence.UnverifiedFallback);
        g.Vendor.Should().Be(BiosVendor.Asus);
        // The best-known ASUS content is still there (only the CONFIDENCE label changed).
        var step = g.Steps.Single(s => s.Setting == FirmwareSettingKind.XmpExpo);
        step.ExactValue.Should().NotBeNullOrWhiteSpace();
        step.ExactValue.Should().Contain("Ai Overclock Tuner"); // exact ASUS path/name
        step.InstructionKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact] // §9.1 invariant: NO shipped board renders as Verified (the KB has no board entries yet)
    public void No_shipped_board_renders_as_verified()
    {
        foreach (var (mfr, product) in new[]
                 {
                     ("ASUSTeK COMPUTER INC.", "ROG STRIX B650-A GAMING WIFI"),
                     ("Micro-Star International Co., Ltd.", "MAG B650 TOMAHAWK WIFI"),
                     ("Gigabyte Technology Co., Ltd.", "B650 AORUS ELITE AX"),
                     ("ASRock", "B650E Steel Legend"),
                     ("Some OEM Ltd", "Mystery Board"),
                 })
        {
            var g = Generate(Hw(manufacturer: mfr, product: product),
                             Finding(FirmwareSettingKind.XmpExpo));
            g.Confidence.Should().Be(GuideConfidence.UnverifiedFallback,
                $"no board+BIOS KB entry exists for {mfr} {product}");
        }
    }

    // ── D5 / §16: uncovered board → fallback labelled Unverified + exact name + Clear-CMOS. ──

    [Fact]
    public void Uncovered_board_is_unverified_with_setting_name_and_clear_cmos()
    {
        var g = Generate(Hw(manufacturer: "Some OEM Ltd", product: "Mystery Board"),
                         Finding(FirmwareSettingKind.XmpExpo));

        g.Confidence.Should().Be(GuideConfidence.UnverifiedFallback);
        // Even unverified, the exact setting NAME must be present (no third-party needed).
        var step = g.Steps.Single(s => s.Setting == FirmwareSettingKind.XmpExpo);
        step.ExactValue.Should().NotBeNullOrWhiteSpace();
        step.ExactValue.Should().Contain("XMP"); // generic fallback names the setting

        // Clear-CMOS recovery must be present (the anti-panic net).
        AllRecoverySteps(g).Should().Contain(s => s.ExactValue == null || true);
        AllRecoveryInstructionKeys(g).Should().Contain(BiosGuideStringKeys.RecoveryClearCmos);
    }

    // ── D4 / ReBAR: ordered dependencies Above-4G → CSM → Re-Size BAR (UEFI). ──

    [Fact]
    public void Rebar_chain_is_ordered_above4g_then_rebar()
    {
        // D4 (lot L5) : le CSM ne fait plus partie du pas a pas. Ce test verifiait l'ordre
        // Above4G -> CSM -> ReBAR ; il verifie desormais celui qui reste, et surtout que le CSM
        // en est bien absent. Above4G reste : ce n'est pas une etape de plus, c'est la moitie
        // mecanique de ReBAR — sans elle, ReBAR ne s'active pas sur la plupart des cartes.
        var g = Generate(Hw(isUefi: true),
            Finding(FirmwareSettingKind.Above4G),
            Finding(FirmwareSettingKind.Csm, action: BiosRecommendedAction.Disable),
            Finding(FirmwareSettingKind.ReBar));

        g.Steps.Should().NotContain(s => s.Setting == FirmwareSettingKind.Csm,
            "un guide vague qui envoie un client couper son CSM, c'est le SAV qui explose");

        var order = g.Steps.Where(s => s.Setting is FirmwareSettingKind.Above4G
                                        or FirmwareSettingKind.ReBar)
                           .Select(s => s.Setting).ToList();

        order.IndexOf(FirmwareSettingKind.Above4G)
            .Should().BeLessThan(order.IndexOf(FirmwareSettingKind.ReBar),
                "le prerequis se lit avant ce qu'il debloque");
    }

    // ── D4 / B2 / §16: Legacy boot → never a CSM-off step; warning + flag instead. ──

    [Fact]
    public void Legacy_boot_never_emits_csm_off_step()
    {
        var g = Generate(Hw(isUefi: false),
            Finding(FirmwareSettingKind.Above4G),
            Finding(FirmwareSettingKind.Csm, action: BiosRecommendedAction.Disable),
            Finding(FirmwareSettingKind.ReBar));

        g.BootMode.Should().Be(BootMode.Legacy);
        g.Steps.Should().NotContain(s => s.Setting == FirmwareSettingKind.Csm,
            "advising CSM-off on a Legacy/MBR system risks no-boot");
        // Le drapeau garde son sens exact : « on n'a pas conseille le CSM parce que tu demarres
        // en Legacy ». C'est ce que la note affichee dit mot pour mot, et elle serait fausse sur
        // une machine UEFI.
        g.Recovery.CsmAdviceSuppressed.Should().BeTrue();
        // Et la ligne de secours « si ReBAR refuse, coupe le CSM » ne doit pas sortir ici :
        // elle enverrait quelqu'un couper un CSM dont sa machine a besoin pour demarrer.
        g.Recovery.Top.Should().NotContain(
            s => s.InstructionKey == "Bios.Guide.Recovery.RebarNeedsCsmOff");
        // The rest of the chain still appears.
        g.Steps.Should().Contain(s => s.Setting == FirmwareSettingKind.Above4G);
        g.Steps.Should().Contain(s => s.Setting == FirmwareSettingKind.ReBar);
    }

    [Fact] // §16 invariant: a guide can never contain a CSM-off step unless boot is UEFI
    public void NonUefi_never_yields_a_csm_step_invariant()
    {
        foreach (var uefi in new[] { false }) // Legacy; Unknown is not expressible via BiosInfo.IsUefi
        {
            var g = Generate(Hw(isUefi: uefi),
                Finding(FirmwareSettingKind.Csm, action: BiosRecommendedAction.Disable));
            g.BootMode.Should().NotBe(BootMode.Uefi);
            g.Steps.Should().NotContain(s => s.Setting == FirmwareSettingKind.Csm);
            g.Recovery.CsmAdviceSuppressed.Should().BeTrue();
        }
    }

    [Fact]
    public void Uefi_boot_names_csm_in_the_recovery_ladder_instead_of_walking_you_through_it()
    {
        // D4 : ce test exigeait une ETAPE CSM dans le pas a pas. Elle n'existe plus — c'etait
        // l'etape la plus dangereuse du guide, et on la conseillait a l'aveugle (SettingsState.Csm
        // vaut Unknown en permanence). Ce qui reste, sur un demarrage UEFI ou c'est sans risque de
        // le dire : le CSM est nomme dans le filet de recuperation, pour que quelqu'un dont ReBAR
        // refuse de s'activer sache ou regarder.
        var g = Generate(Hw(isUefi: true),
            Finding(FirmwareSettingKind.Csm, action: BiosRecommendedAction.Disable));

        g.BootMode.Should().Be(BootMode.Uefi);
        g.Steps.Should().NotContain(s => s.Setting == FirmwareSettingKind.Csm);
        g.Recovery.CsmAdviceSuppressed.Should().BeFalse("rien n'a ete supprime POUR CAUSE de Legacy");
        g.Recovery.Top.Should().Contain(
            s => s.InstructionKey == "Bios.Guide.Recovery.RebarNeedsCsmOff",
            "on ne fait pas faire, mais on ne laisse pas en plan non plus");
    }

    // ── D2: recovery section mandatory TOP and BOTTOM with the full ladder. ──

    [Fact]
    public void Recovery_section_present_top_and_bottom_with_full_ladder()
    {
        var g = Generate(Hw(), Finding(FirmwareSettingKind.XmpExpo));

        g.Recovery.Top.Should().NotBeEmpty();
        g.Recovery.Bottom.Should().NotBeEmpty();

        foreach (var section in new[] { g.Recovery.Top, g.Recovery.Bottom })
        {
            var keys = section.Select(s => s.InstructionKey).ToList();
            keys.Should().ContainInOrder(
                BiosGuideStringKeys.RecoveryNoBootNormal,
                BiosGuideStringKeys.RecoveryMemoryTraining,
                BiosGuideStringKeys.RecoveryClearCmos,
                BiosGuideStringKeys.RecoveryLoadDefaults,
                BiosGuideStringKeys.RecoveryOneChangeAtATime);
            section.Should().OnlyContain(s => s.IsRecoveryRelated);
        }
    }

    // ── Non-Guide findings (read/auto/lab/forbidden) → NO write step. ──

    [Theory]
    [InlineData(BiosMatrixClass.Read)]
    [InlineData(BiosMatrixClass.AutoOfficial)]
    [InlineData(BiosMatrixClass.LabWhitelist)]
    [InlineData(BiosMatrixClass.Forbidden)]
    public void NonGuide_findings_yield_no_write_step(BiosMatrixClass cls)
    {
        var g = Generate(Hw(), Finding(FirmwareSettingKind.PcieGen, cls: cls));
        g.Steps.Should().BeEmpty("only Guide-class findings produce write steps");
    }

    [Fact]
    public void Mixed_findings_keep_only_guide_class_steps()
    {
        var g = Generate(Hw(),
            Finding(FirmwareSettingKind.XmpExpo, cls: BiosMatrixClass.Guide),
            Finding(FirmwareSettingKind.PcieGen, cls: BiosMatrixClass.Read),
            Finding(FirmwareSettingKind.BiosVersion, cls: BiosMatrixClass.Read));

        g.Steps.Select(s => s.Setting).Should().Equal(FirmwareSettingKind.XmpExpo);
    }

    // ── No findings → an empty-steps guide that still has the recovery section. ──

    [Fact]
    public void No_findings_yields_steps_empty_but_recovery_present()
    {
        var g = Generate(Hw());
        g.Steps.Should().BeEmpty();
        g.Recovery.Top.Should().NotBeEmpty();
        g.Recovery.Bottom.Should().NotBeEmpty();
    }

    // ── Every step carries a resx instruction key (zero hard-coded UI text). ──

    [Fact]
    public void Every_step_carries_a_resx_key()
    {
        var g = Generate(Hw(),
            Finding(FirmwareSettingKind.XmpExpo),
            Finding(FirmwareSettingKind.Above4G),
            Finding(FirmwareSettingKind.ReBar),
            Finding(FirmwareSettingKind.X3dCppc));

        g.Steps.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.InstructionKey));
        AllRecoverySteps(g).Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.InstructionKey));
    }

    // ── The guide carries the enter/save keys from the resolved template. ──

    [Fact]
    public void Guide_carries_enter_and_save_keys_from_the_template()
    {
        var g = Generate(Hw(), Finding(FirmwareSettingKind.XmpExpo));

        // The resolved template's keys pass through verbatim so the guide tells the user
        // how to enter the BIOS and save & exit.
        g.EnterKey.Should().NotBeNullOrWhiteSpace();
        g.SaveKey.Should().NotBeNullOrWhiteSpace();
    }

    private static IEnumerable<GuideStep> AllRecoverySteps(PersonalizedGuide g)
        => g.Recovery.Top.Concat(g.Recovery.Bottom);

    private static IEnumerable<string> AllRecoveryInstructionKeys(PersonalizedGuide g)
        => AllRecoverySteps(g).Select(s => s.InstructionKey);
}
