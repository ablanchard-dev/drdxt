using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// M3 — the scan surfaces the BIOS Doctor's firmware findings (the integration the M3
/// audit flagged as missing: the advisor existed but was wired to nothing, so the user
/// never saw their biggest real lever — XMP/EXPO off, which lives in the BIOS).
///
/// Invariants proven here:
///   • findings are surfaced after a successful scan, ordered fix-misconfig-first
///     (actionable GUIDE findings before informational/not-readable, highest severity first);
///   • an XMP/EXPO-off finding carries its SOURCED gain range and is GUIDANCE-only;
///   • a not-readable state is marked "to verify", never asserted;
///   • a failing advisor leaves the section empty and never breaks the (free) scan;
///   • a rescan clears the previous section.
/// </summary>
public class ScanBiosFindingsTests
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

    // ── Finding builders ──────────────────────────────────────────────────
    private static BiosFinding Guide(FirmwareSettingKind kind, Severity sev, BiosGainEstimate? gain = null) =>
        new(kind, sev, $"{kind}.Title", $"{kind}.Rationale", BiosMatrixClass.Guide,
            TriState.Off, BiosRecommendedAction.Enable, $"{kind}.Action", gain, StateNotReadable: false);

    private static BiosFinding ReadInfo(FirmwareSettingKind kind, bool notReadable = true) =>
        new(kind, Severity.Info, $"{kind}.Title", $"{kind}.Rationale", BiosMatrixClass.Read,
            TriState.Unknown, BiosRecommendedAction.Verify, $"{kind}.Action", Gain: null, StateNotReadable: notReadable);

    private static readonly BiosGainEstimate XmpGain =
        new(MinPct: 11.0, MaxPct: 25.0, Conditional: false, SourceKey: "src", CaveatKey: "caveat");

    private static ScanViewModel BuildWithAdvisor(IBiosAdvisor advisor)
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

        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key, advisor);
    }

    [Fact]
    public async Task Surfaced_bios_findings_are_ordered_fix_misconfig_first()
    {
        // Supplied in a deliberately scrambled order: an informational Read finding, then the
        // Major GUIDE (XMP off), then a Minor GUIDE (ReBAR). Expected surfaced order:
        // GUIDE-before-Read, highest severity first → XMP (Major Guide), ReBAR (Minor Guide), PCIe (Info Read).
        var advisor = ScanViewModelTestSeams.BiosAdvisor(
            ReadInfo(FirmwareSettingKind.PcieGen),
            Guide(FirmwareSettingKind.XmpExpo, Severity.Major, XmpGain),
            Guide(FirmwareSettingKind.ReBar, Severity.Minor));

        var vm = BuildWithAdvisor(advisor);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasBiosFindings.Should().BeTrue();
        vm.BiosFindings.Select(f => f.Kind).Should().ContainInOrder(
            FirmwareSettingKind.XmpExpo.ToString(),
            FirmwareSettingKind.ReBar.ToString(),
            FirmwareSettingKind.PcieGen.ToString());
    }

    [Fact]
    public async Task Xmp_off_finding_carries_sourced_gain_range_and_is_guidance_only()
    {
        var advisor = ScanViewModelTestSeams.BiosAdvisor(Guide(FirmwareSettingKind.XmpExpo, Severity.Major, XmpGain));

        var vm = BuildWithAdvisor(advisor);
        await vm.ScanCommand.ExecuteAsync(null);

        var xmp = vm.BiosFindings.Single();
        xmp.IsGuidanceOnly.Should().BeTrue("an actionable BIOS fix is guided, never written by DrDXT");
        xmp.GuidanceLabel.Should().NotBeNullOrEmpty();
        // A sourced RANGE, never a bare promised point-%.
        xmp.GainLabel.Should().Contain("11").And.Contain("25");
        xmp.IsGainConditional.Should().BeFalse();
    }

    [Fact]
    public async Task State_not_readable_finding_is_marked_to_verify()
    {
        var advisor = ScanViewModelTestSeams.BiosAdvisor(ReadInfo(FirmwareSettingKind.PcieGen, notReadable: true));

        var vm = BuildWithAdvisor(advisor);
        await vm.ScanCommand.ExecuteAsync(null);

        var f = vm.BiosFindings.Single();
        f.StateNotReadable.Should().BeTrue();
        f.UnverifiedLabel.Should().NotBeNullOrEmpty();
        f.IsGuidanceOnly.Should().BeFalse("a not-readable read finding proposes no change");
        f.GainLabel.Should().BeEmpty("an unquantified finding shows no gain number");
    }

    [Fact]
    public async Task Advisor_failure_leaves_bios_section_empty_and_scan_succeeds()
    {
        var vm = BuildWithAdvisor(ScanViewModelTestSeams.BiosAdvisorFailing());

        await vm.ScanCommand.ExecuteAsync(null);

        vm.HasBiosFindings.Should().BeFalse();
        vm.BiosFindings.Should().BeEmpty();
        // The BIOS section is additive: a fault must never break the free scan.
        vm.State.Should().Be(ScanState.Done);
    }

    [Fact]
    public async Task Rescan_clears_the_previous_bios_section()
    {
        var advisor = Substitute.For<IBiosAdvisor>();
        advisor.Advise(Arg.Any<HardwareInventory>(), Arg.Any<SettingsState>())
            .Returns(
                Result<IReadOnlyList<BiosFinding>>.Ok(new[] { Guide(FirmwareSettingKind.XmpExpo, Severity.Major, XmpGain) }),
                Result<IReadOnlyList<BiosFinding>>.Ok(Array.Empty<BiosFinding>()));

        var vm = BuildWithAdvisor(advisor);

        await vm.ScanCommand.ExecuteAsync(null);
        vm.BiosFindings.Should().HaveCount(1);

        await vm.ScanCommand.ExecuteAsync(null);
        vm.BiosFindings.Should().BeEmpty("a rescan replaces the previous section");
        vm.HasBiosFindings.Should().BeFalse();
    }
}
