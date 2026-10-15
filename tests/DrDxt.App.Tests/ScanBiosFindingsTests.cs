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
/// Le scan fait remonter les constats firmware du docteur BIOS (l'intégration que l'audit du
/// travail BIOS signalait manquante : le conseiller existait mais n'était câblé à rien, donc le
/// joueur ne voyait jamais son plus gros vrai levier, XMP/EXPO désactivé, qui vit dans le BIOS).
///
/// Invariants prouvés ici :
///   • les constats remontent après un scan réussi, mauvaise configuration d'abord
///     (constats guidés actionnables avant les informatifs/non lisibles, gravité la plus haute d'abord) ;
///   • un constat XMP/EXPO désactivé porte sa fourchette de gain sourcée et n'est qu'un guide ;
///   • un état non lisible est marqué « à vérifier », jamais affirmé ;
///   • un conseiller en échec laisse la section vide et ne casse jamais le scan (gratuit) ;
///   • un nouveau scan efface la section précédente.
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

    // ── Constructeurs de constats ─────────────────────────────────────────
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
          .Returns(Result<ScanResult>.Ok(new ScanResult(Hw(), St, new List<Finding>(), null)));

        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(
            new List<StorageDevice> { new("Samsung 990 Pro", "NVMe", 1L << 40) }));

        return ScanViewModelTestSeams.Build(hd, sr, br, sc, sd, key => key, advisor);
    }

    [Fact]
    public async Task Surfaced_bios_findings_are_ordered_fix_misconfig_first()
    {
        // Fournis dans un ordre volontairement mélangé : un constat lu informatif, puis le guide
        // majeur (XMP désactivé), puis un guide mineur (ReBAR). Ordre attendu en sortie :
        // guide avant lecture, gravité la plus haute d'abord → XMP (guide majeur), ReBAR (guide mineur), PCIe (lecture informative).
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
        // Une fourchette sourcée, jamais un % ponctuel promis.
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
        // La section BIOS est additive : une faute ne doit jamais casser le scan gratuit.
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
