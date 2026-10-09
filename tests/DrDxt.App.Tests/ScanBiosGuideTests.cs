using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// M3 — the scan surfaces the PERSONALIZED BIOS guide (the "HOW", not just the "WHAT"): the
/// exact menu path → setting → value to set on the user's board, the mandatory recovery
/// section, and an HONEST confidence label.
///
/// Invariants proven here:
///   • a scan with actionable Guide findings surfaces BiosGuide with ordered steps carrying
///     their exact value;
///   • an UnverifiedFallback guide is labelled unverified and never claims to be verified;
///   • the recovery section (top + bottom) is mandatory and non-empty;
///   • when CSM-off was suppressed (Legacy boot) the honest note surfaces;
///   • a guide with no actionable step (recovery-only) leaves BiosGuide null;
///   • a failing generator leaves BiosGuide null and never breaks the (free) scan.
/// </summary>
public class ScanBiosGuideTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, false, 1),
        new[] { new GpuInfo("GPU", "AMD", 16L << 30, "1", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 4800) },
        new MotherboardInfo("MSI", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState St => new(
        TriState.On, TriState.On, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    // A Guide-class advisor finding — its only job here is to make the advisor return a
    // non-empty raw list so the (substituted) generator is invoked. The generator itself is
    // a fake whose returned guide drives the assertions.
    private static BiosFinding AdvisorFinding() =>
        new(FirmwareSettingKind.XmpExpo, Severity.Major, "XmpExpo.Title", "XmpExpo.Rationale",
            BiosMatrixClass.Guide, TriState.Off, BiosRecommendedAction.Enable, "XmpExpo.Action",
            Gain: null, StateNotReadable: false);

    // A BIOS advisor that returns one actionable finding (so the guide generator runs).
    private static IBiosAdvisor AdvisorWithFinding() =>
        ScanViewModelTestSeams.BiosAdvisor(AdvisorFinding());

    private static ScanViewModel BuildWith(IBiosAdvisor advisor, IGuideGenerator generator)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(Result<HardwareInventory>.Ok(Hw()));

        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(St));

        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Ok(
              new BenchmarkResult(142.7, 98.3, 75.0, new List<double> { 7.0 }, null, 2.1, 5)));

        var sc = Substitute.For<IScanScorer>();
        sc.Score(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>(), Arg.Any<BenchmarkResult?>())
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), 72, null,
              new ScoreBreakdown(80, 80, 85), new DimensionStatus(true, false))));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key, advisor, generator);
    }

    [Fact]
    public async Task Surfaced_guide_has_ordered_steps_with_exact_value()
    {
        var guide = ScanViewModelTestSeams.Guide(
            GuideConfidence.Verified, false, "MSI MAG B650 TOMAHAWK WIFI",
            ScanViewModelTestSeams.Step(FirmwareSettingKind.XmpExpo, "Bios.Guide.Step.Xmp",
                "Ai Tweaker  →  EXPO  →  = EXPO I", 1),
            ScanViewModelTestSeams.Step(FirmwareSettingKind.ReBar, "Bios.Guide.Step.ReBar",
                "Settings  →  Re-Size BAR Support  →  = Enabled", 2));

        var vm = BuildWith(AdvisorWithFinding(), ScanViewModelTestSeams.GuideGenerator(guide));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasBiosGuide.Should().BeTrue();
        vm.BiosGuide!.BoardLabel.Should().Be("MSI MAG B650 TOMAHAWK WIFI");
        vm.BiosGuide.Steps.Should().HaveCount(2);
        // Order preserved; the localized instruction + the RAW exact value both surface.
        vm.BiosGuide.Steps[0].Instruction.Should().Be("Bios.Guide.Step.Xmp");
        vm.BiosGuide.Steps[0].ExactValue.Should().Contain("EXPO I");
        vm.BiosGuide.Steps[1].ExactValue.Should().Contain("Re-Size BAR Support");
    }

    [Fact]
    public async Task Unverified_fallback_guide_is_labelled_unverified_and_never_claims_verified()
    {
        var guide = ScanViewModelTestSeams.Guide(
            GuideConfidence.UnverifiedFallback, false, "Generic", ScanViewModelTestSeams.Step());

        var vm = BuildWith(AdvisorWithFinding(), ScanViewModelTestSeams.GuideGenerator(guide));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosGuide!.IsVerified.Should().BeFalse("a best-known fallback is never verified for the exact board");
        vm.BiosGuide.ConfidenceLabel.Should().NotBeNullOrEmpty();
        // Honesty: the label is the UNVERIFIED key, never the verified one (identity localize).
        vm.BiosGuide.ConfidenceLabel.Should().Be("Scan.Bios.Guide.Unverified");
        vm.BiosGuide.ConfidenceLabel.Should().NotBe("Scan.Bios.Guide.Verified");
    }

    [Fact]
    public async Task Verified_guide_is_labelled_verified()
    {
        var guide = ScanViewModelTestSeams.Guide(
            GuideConfidence.Verified, false, "ROG STRIX B650-A", ScanViewModelTestSeams.Step());

        var vm = BuildWith(AdvisorWithFinding(), ScanViewModelTestSeams.GuideGenerator(guide));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosGuide!.IsVerified.Should().BeTrue();
        vm.BiosGuide.ConfidenceLabel.Should().Be("Scan.Bios.Guide.Verified");
    }

    [Fact]
    public async Task Recovery_top_and_bottom_are_non_empty()
    {
        var guide = ScanViewModelTestSeams.Guide(
            GuideConfidence.Verified, false, "Board", ScanViewModelTestSeams.Step());

        var vm = BuildWith(AdvisorWithFinding(), ScanViewModelTestSeams.GuideGenerator(guide));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosGuide!.RecoveryTop.Should().NotBeEmpty("the recovery section is mandatory at the top");
        vm.BiosGuide.RecoveryBottom.Should().NotBeEmpty("the recovery section is mandatory at the bottom");
    }

    [Fact]
    public async Task Csm_suppressed_surfaces_its_note()
    {
        var guide = ScanViewModelTestSeams.Guide(
            GuideConfidence.Verified, csmSuppressed: true, board: "Board",
            ScanViewModelTestSeams.Step());

        var vm = BuildWith(AdvisorWithFinding(), ScanViewModelTestSeams.GuideGenerator(guide));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosGuide!.CsmSuppressed.Should().BeTrue();
        vm.BiosGuide.CsmSuppressedNote.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Guide_with_no_actionable_steps_leaves_bios_guide_null()
    {
        // A recovery-only guide (no actionable step) is nothing to guide → BiosGuide stays null.
        var recoveryOnly = ScanViewModelTestSeams.Guide(); // default: empty steps
        var vm = BuildWith(AdvisorWithFinding(), ScanViewModelTestSeams.GuideGenerator(recoveryOnly));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasBiosGuide.Should().BeFalse();
        vm.BiosGuide.Should().BeNull();
        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task Surfaced_guide_carries_enter_and_save_keys_with_localized_instructions()
    {
        var guide = ScanViewModelTestSeams.GuideWithKeys("Del", "F10");

        var vm = BuildWith(AdvisorWithFinding(), ScanViewModelTestSeams.GuideGenerator(guide));
        await vm.ScanCommand.ExecuteAsync(null);

        // The raw keys surface verbatim.
        vm.BiosGuide!.EnterKey.Should().Be("Del");
        vm.BiosGuide.SaveKey.Should().Be("F10");
        // The localized instruction names the key (identity localize returns the template).
        vm.BiosGuide.EnterInstruction.Should().Contain("Del");
        vm.BiosGuide.SaveInstruction.Should().Contain("F10");
    }

    [Fact]
    public async Task Blank_enter_or_save_key_yields_an_empty_instruction()
    {
        // An honest empty key must never render "Press  to enter".
        var guide = ScanViewModelTestSeams.GuideWithKeys(enterKey: "", saveKey: "  ");

        var vm = BuildWith(AdvisorWithFinding(), ScanViewModelTestSeams.GuideGenerator(guide));
        await vm.ScanCommand.ExecuteAsync(null);

        vm.BiosGuide!.EnterKey.Should().BeEmpty();
        vm.BiosGuide.EnterInstruction.Should().BeEmpty();
        vm.BiosGuide.SaveInstruction.Should().BeEmpty();
    }

    [Fact]
    public async Task Failing_generator_leaves_guide_null_and_scan_completes()
    {
        var vm = BuildWith(AdvisorWithFinding(), ScanViewModelTestSeams.GuideGeneratorFailing());

        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasBiosGuide.Should().BeFalse();
        vm.BiosGuide.Should().BeNull();
        // The guide is additive: a fault must never break the free scan.
        vm.State.Should().Be(ScanState.Done);
    }
}

/// <summary>
/// M3 lot 2 — « emmene-moi dans le BIOS ». DrDXT peut demander a Windows d'envoyer le prochain
/// demarrage dans le setup firmware (shutdown /r /fw) au lieu de demander a l'utilisateur de
/// marteler une touche au demarrage.
///
/// L'invariant qui compte n'est pas le bouton : c'est que le chemin MANUEL ne disparaisse
/// jamais. Un produit qui masque la touche parce qu'il croit avoir mieux laisse le client sans
/// rien le jour ou le mieux ne marche pas.
/// </summary>
public sealed class BiosGuideFirmwareTripTests
{
    private static PersonalizedGuide AGuide() =>
        ScanViewModelTestSeams.Guide(steps: new GuideStep(1, "Bios.Guide.Step.Xmp", "Profile1", FirmwareSettingKind.XmpExpo, true));

    private static string Localize(string k) => k;

    [Fact]
    public async Task Without_the_seam_the_capability_is_false_and_the_call_FAILS_instead_of_doing_nothing()
    {
        var vm = new BiosGuideViewModel(AGuide(), Localize);

        vm.CanOpenFirmwareDirectly.Should().BeFalse();

        var r = await vm.OpenFirmwareSetupAsync();
        r.IsOk.Should().BeFalse();   // dit, jamais un silence
    }

    [Fact]
    public async Task With_the_seam_the_delay_is_passed_through()
    {
        int? seen = null;
        var vm = new BiosGuideViewModel(AGuide(), Localize, d =>
        {
            seen = d;
            return Task.FromResult(Result.Ok());
        });

        vm.CanOpenFirmwareDirectly.Should().BeTrue();
        (await vm.OpenFirmwareSetupAsync(25)).IsOk.Should().BeTrue();
        seen.Should().Be(25);
    }

    [Fact]
    public async Task The_default_delay_stays_inside_the_band_the_allowlist_accepts()
    {
        // Corrige le 26/08 : ce test reecrivait la bande EN DUR (BeInRange(0, 300)), donc il
        // ne reliait rien a rien. Resserrer l'allowlist a 0..60 l'aurait laisse vert avec un
        // bouton qui echoue a chaque clic -- le scenario que son propre commentaire disait
        // vouloir prevenir. On interroge maintenant la vraie allowlist.
        new DrDxt.ElevatedHelper.OpAllowlist()
            .Validate(new RebootToFirmwareSetupOp(BiosGuideViewModel.DefaultFirmwareRebootDelaySeconds))
            .IsOk.Should().BeTrue();

        int? seen = null;
        var vm = new BiosGuideViewModel(AGuide(), Localize, d => { seen = d; return Task.FromResult(Result.Ok()); });
        await vm.OpenFirmwareSetupAsync();
        seen.Should().Be(BiosGuideViewModel.DefaultFirmwareRebootDelaySeconds);
    }

    [Fact]
    public void The_manual_key_instruction_SURVIVES_the_button()
    {
        // L'invariant du lot. Le bouton est un confort ; si le firmware refuse le
        // boot-to-setup, l'utilisateur doit toujours avoir la touche sous les yeux.
        var sans = new BiosGuideViewModel(AGuide(), Localize);
        var avec = new BiosGuideViewModel(AGuide(), Localize, _ => Task.FromResult(Result.Ok()));

        avec.EnterKey.Should().Be("Del");
        avec.EnterInstruction.Should().Be(sans.EnterInstruction);
        avec.EnterInstruction.Should().NotBeNullOrWhiteSpace();
    }
}
