using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// -----------------------------------------------------------------------
// Tests de fumée des contrats : un test d'égalité de valeur par record,
// un test de membre par énumération. La présence d'une interface = compiler suffit.
// -----------------------------------------------------------------------

public class EnumTests
{
    // --- ChassisType ---
    [Fact]
    public void ChassisType_HasExpectedMembers()
    {
        var names = Enum.GetNames<ChassisType>();
        names.Should().Contain(new[] { "Desktop", "Laptop", "Handheld", "Unknown" });
        names.Should().HaveCount(4);
    }

    // --- TriState ---
    [Fact]
    public void TriState_HasExpectedMembers()
    {
        var names = Enum.GetNames<TriState>();
        names.Should().Contain(new[] { "On", "Off", "Unknown" });
        names.Should().HaveCount(3);
    }

    // --- Severity ---
    [Fact]
    public void Severity_HasExpectedMembers()
    {
        var names = Enum.GetNames<Severity>();
        names.Should().Contain(new[] { "Info", "Minor", "Major" });
        names.Should().HaveCount(3);
    }

    // --- TweakCategory ---
    [Fact]
    public void TweakCategory_HasExpectedMembers()
    {
        var names = Enum.GetNames<TweakCategory>();
        names.Should().Contain(new[] { "Latency", "GpuTuning", "PowerPlan", "FixIt", "Bios" });
        names.Should().HaveCount(5);
    }

    // --- DriftCause ---
    [Fact]
    public void DriftCause_HasExpectedMembers()
    {
        var names = Enum.GetNames<DriftCause>();
        names.Should().Contain(new[] { "WindowsReset", "UserIntentional", "Unknown" });
        names.Should().HaveCount(3);
    }

    // --- LeverState ---
    [Fact]
    public void LeverState_HasExpectedMembers()
    {
        var names = Enum.GetNames<LeverState>();
        names.Should().Contain(new[] { "Optimal", "Suboptimal", "Unknown", "NotApplicable" });
        names.Should().HaveCount(4);
    }
}

public class RecordEqualityTests
{
    // --- CpuInfo ---
    [Fact]
    public void CpuInfo_EqualWhenFieldsMatch()
    {
        var a = new CpuInfo("Ryzen 9 7900X", "AMD", 12, 24, false, 1);
        var b = new CpuInfo("Ryzen 9 7900X", "AMD", 12, 24, false, 1);
        a.Should().Be(b);
    }

    [Fact]
    public void CpuInfo_NotEqualWhenFieldDiffers()
    {
        var a = new CpuInfo("Ryzen 9 7900X", "AMD", 12, 24, false, 1);
        var b = new CpuInfo("Ryzen 9 7900X3D", "AMD", 12, 24, true, 1);
        a.Should().NotBe(b);
    }

    // --- GpuInfo ---
    [Fact]
    public void GpuInfo_EqualWhenFieldsMatch()
    {
        var a = new GpuInfo("RTX 4090", "NVIDIA", 24L * 1024 * 1024 * 1024, "546.33", TriState.Unknown);
        var b = new GpuInfo("RTX 4090", "NVIDIA", 24L * 1024 * 1024 * 1024, "546.33", TriState.Unknown);
        a.Should().Be(b);
    }

    [Fact]
    public void GpuInfo_NotEqualWhenVramDiffers()
    {
        var a = new GpuInfo("RTX 4090", "NVIDIA", 24L * 1024 * 1024 * 1024, "546.33", TriState.Unknown);
        var b = new GpuInfo("RTX 4090", "NVIDIA", 16L * 1024 * 1024 * 1024, "546.33", TriState.Unknown);
        a.Should().NotBe(b);
    }

    // --- MemoryModule ---
    [Fact]
    public void MemoryModule_EqualWhenFieldsMatch()
    {
        var a = new MemoryModule("CMK32GX5M2D6000Z36", "Corsair", 6000, 6000);
        var b = new MemoryModule("CMK32GX5M2D6000Z36", "Corsair", 6000, 6000);
        a.Should().Be(b);
    }

    [Fact]
    public void MemoryModule_NotEqualWhenConfiguredMhzDiffers()
    {
        var a = new MemoryModule("CMK32GX5M2D6000Z36", "Corsair", 4800, 6000);
        var b = new MemoryModule("CMK32GX5M2D6000Z36", "Corsair", 6000, 6000);
        a.Should().NotBe(b);
    }

    // --- MotherboardInfo ---
    [Fact]
    public void MotherboardInfo_EqualWhenFieldsMatch()
    {
        var a = new MotherboardInfo("ASUS", "ROG CROSSHAIR X670E");
        var b = new MotherboardInfo("ASUS", "ROG CROSSHAIR X670E");
        a.Should().Be(b);
    }

    // --- BiosInfo ---
    [Fact]
    public void BiosInfo_EqualWhenFieldsMatch()
    {
        var date = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var a = new BiosInfo("1802", date, true);
        var b = new BiosInfo("1802", date, true);
        a.Should().Be(b);
    }

    [Fact]
    public void BiosInfo_NullableReleaseDateAllowed()
    {
        var a = new BiosInfo("1802", null, true);
        a.ReleaseDate.Should().BeNull();
    }

    // --- HardwareInventory ---
    [Fact]
    public void HardwareInventory_EqualWhenFieldsMatch()
    {
        var cpu = new CpuInfo("i9-14900K", "Intel", 24, 32, false, 0);
        var gpu = new GpuInfo("RTX 4080", "NVIDIA", 16L * 1024 * 1024 * 1024, "546.01", TriState.On);
        var mem = new MemoryModule("BLS16G4D32AESB", "Crucial", 3200, 3200);
        var board = new MotherboardInfo("MSI", "Z790 ACE");
        var bios = new BiosInfo("7D25vAA", null, true);
        // Partager les instances de liste : IReadOnlyList<T> se compare par référence dans le Equals des records
        IReadOnlyList<GpuInfo> gpus = new[] { gpu };
        IReadOnlyList<MemoryModule> mems = new[] { mem };

        var a = new HardwareInventory(cpu, gpus, mems, board, bios, ChassisType.Desktop);
        var b = new HardwareInventory(cpu, gpus, mems, board, bios, ChassisType.Desktop);
        a.Should().Be(b);
    }

    // --- SettingsState ---
    [Fact]
    public void SettingsState_EqualWhenFieldsMatch()
    {
        var a = new SettingsState(TriState.On, TriState.Unknown, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);
        var b = new SettingsState(TriState.On, TriState.Unknown, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);
        a.Should().Be(b);
    }

    // --- BenchmarkResult ---
    [Fact]
    public void BenchmarkResult_EqualWhenFieldsMatch()
    {
        var frames = new double[] { 16.6, 16.7, 16.5 };
        var a = new BenchmarkResult(120.0, 95.0, 78.0, frames, 12.5, 2.0, 3);
        var b = new BenchmarkResult(120.0, 95.0, 78.0, frames, 12.5, 2.0, 3);
        a.Should().Be(b);
    }

    [Fact]
    public void BenchmarkResult_NullableEstimatedPcLatencyAllowed()
    {
        var a = new BenchmarkResult(120.0, 95.0, 78.0, Array.Empty<double>(), null, 2.0, 3);
        a.EstimatedPcLatencyMs.Should().BeNull();
    }

    // --- AbVerdict ---
    [Fact]
    public void AbVerdict_EqualWhenFieldsMatch()
    {
        var before = new BenchmarkResult(100.0, 80.0, 65.0, Array.Empty<double>(), null, 2.0, 1);
        var after = new BenchmarkResult(115.0, 90.0, 72.0, Array.Empty<double>(), null, 2.0, 1);
        var a = new AbVerdict(before, after, true, "GAIN_MEASURABLE");
        var b = new AbVerdict(before, after, true, "GAIN_MEASURABLE");
        a.Should().Be(b);
    }

    // --- Finding ---
    [Fact]
    public void Finding_EqualWhenFieldsMatch()
    {
        var a = new Finding("XMP", Severity.Major, "XMP_DISABLED", 8.5);
        var b = new Finding("XMP", Severity.Major, "XMP_DISABLED", 8.5);
        a.Should().Be(b);
    }

    [Fact]
    public void Finding_NullableEstimatedGainPctAllowed()
    {
        var a = new Finding("BIOS", Severity.Info, "BIOS_OLD", null);
        a.EstimatedGainPct.Should().BeNull();
    }

    // --- ScoreLedgerEntry ---
    [Fact]
    public void ScoreLedgerEntry_EqualWhenFieldsMatch()
    {
        var a = new ScoreLedgerEntry("xmp", "Performance", 15, LeverState.Suboptimal, "Finding.Memory.XmpOff", "Source.RamAll");
        var b = new ScoreLedgerEntry("xmp", "Performance", 15, LeverState.Suboptimal, "Finding.Memory.XmpOff", "Source.RamAll");
        a.Should().Be(b);
    }

    [Fact]
    public void ScoreLedgerEntry_NotEqualWhenPenaltyDiffers()
    {
        var a = new ScoreLedgerEntry("xmp", "Performance", 15, LeverState.Suboptimal, "k", "s");
        var b = new ScoreLedgerEntry("xmp", "Performance", 0, LeverState.Optimal, "k", "s");
        a.Should().NotBe(b);
    }

    // --- ScanCoverage ---
    [Fact]
    public void ScanCoverage_EqualWhenFieldsMatch()
    {
        var a = new ScanCoverage(4, 3, 1);
        var b = new ScanCoverage(4, 3, 1);
        a.Should().Be(b);
    }

    // --- ScanResult ---
    [Fact]
    public void ScanResult_NullableBaselineAllowed()
    {
        var cpu = new CpuInfo("i5-13600K", "Intel", 14, 20, false, 0);
        var board = new MotherboardInfo("Gigabyte", "Z790 GAMING X");
        var bios = new BiosInfo("F9", null, true);
        var hw = new HardwareInventory(cpu, Array.Empty<GpuInfo>(), Array.Empty<MemoryModule>(), board, bios, ChassisType.Desktop);
        var state = new SettingsState(TriState.Off, TriState.Unknown, "plan-guid", TriState.Off, TriState.Off, Array.Empty<int>(), TriState.Unknown);
        var findings = Array.Empty<Finding>();

        var result = new ScanResult(hw, state, findings, null);
        result.Baseline.Should().BeNull();
    }

    // --- TweakSnapshot ---
    [Fact]
    public void TweakSnapshot_EqualWhenFieldsMatch()
    {
        var a = new TweakSnapshot("hags-on", "{\"value\":1}");
        var b = new TweakSnapshot("hags-on", "{\"value\":1}");
        a.Should().Be(b);
    }

    // --- AppliedTweak ---
    [Fact]
    public void AppliedTweak_EqualWhenFieldsMatch()
    {
        var ts = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var a = new AppliedTweak("hags-on", "{\"value\":1}", "{\"value\":0}", ts);
        var b = new AppliedTweak("hags-on", "{\"value\":1}", "{\"value\":0}", ts);
        a.Should().Be(b);
    }

    // --- DesiredState ---
    [Fact]
    public void DesiredState_EqualWhenFieldsMatch()
    {
        var tweaks = Array.Empty<AppliedTweak>();
        var a = new DesiredState(tweaks);
        var b = new DesiredState(tweaks);
        a.Should().Be(b);
    }

    // --- HardwareMatch ---
    [Fact]
    public void HardwareMatch_EqualWhenFieldsMatch()
    {
        var a = new HardwareMatch("Ryzen.*X3D", "RTX.*");
        var b = new HardwareMatch("Ryzen.*X3D", "RTX.*");
        a.Should().Be(b);
    }

    [Fact]
    public void HardwareMatch_NullablePatternsAllowed()
    {
        var a = new HardwareMatch(null, null);
        a.CpuPattern.Should().BeNull();
        a.GpuPattern.Should().BeNull();
    }

    // --- Profile ---
    [Fact]
    public void Profile_EqualWhenFieldsMatch()
    {
        var match = new HardwareMatch(".*", null);
        // Partager la même instance de liste : IReadOnlyList<T> se compare par référence dans le Equals des records
        IReadOnlyList<string> tweakIds = new[] { "hags-on", "xmp" };
        var a = new Profile("amd-x3d-rtx", match, tweakIds, true);
        var b = new Profile("amd-x3d-rtx", match, tweakIds, true);
        a.Should().Be(b);
    }

    // --- TweakPlan ---
    [Fact]
    public void TweakPlan_EqualWhenFieldsMatch()
    {
        // Partager la même instance de liste : IReadOnlyList<T> se compare par référence dans le Equals des records
        IReadOnlyList<string> ids = new[] { "hags-on", "gamemode-on" };
        var a = new TweakPlan(ids);
        var b = new TweakPlan(ids);
        a.Should().Be(b);
    }

    // --- ApplyOutcome ---
    [Fact]
    public void ApplyOutcome_EqualWhenFieldsMatch()
    {
        var snap = new TweakSnapshot("hags-on", "{\"value\":0}");
        var a = new ApplyOutcome("hags-on", true, false, snap, null);
        var b = new ApplyOutcome("hags-on", true, false, snap, null);
        a.Should().Be(b);
    }

    [Fact]
    public void ApplyOutcome_NullableSnapshotAndErrorAllowed()
    {
        var err = new Error("ELEVATION_DENIED", "needs admin");
        var a = new ApplyOutcome("hags-on", false, false, null, err);
        a.Snapshot.Should().BeNull();
        a.Error.Should().NotBeNull();
    }

    // --- ApplyReport ---
    [Fact]
    public void ApplyReport_EqualWhenFieldsMatch()
    {
        var outcomes = Array.Empty<ApplyOutcome>();
        var a = new ApplyReport(outcomes, null);
        var b = new ApplyReport(outcomes, null);
        a.Should().Be(b);
    }

    [Fact]
    public void ApplyReport_NullableVerdictAllowed()
    {
        var a = new ApplyReport(Array.Empty<ApplyOutcome>(), null);
        a.Verdict.Should().BeNull();
    }
}

public class ErrorCodesTests
{
    [Fact]
    public void ErrorCodes_DetectionFailed_HasExpectedValue()
        => ErrorCodes.DetectionFailed.Should().Be("DETECTION_FAILED");

    [Fact]
    public void ErrorCodes_BenchmarkFailed_HasExpectedValue()
        => ErrorCodes.BenchmarkFailed.Should().Be("BENCHMARK_FAILED");

    [Fact]
    public void ErrorCodes_RestorePointBlocked_HasExpectedValue()
        => ErrorCodes.RestorePointBlocked.Should().Be("RESTORE_POINT_BLOCKED");

    [Fact]
    public void ErrorCodes_ElevationDenied_HasExpectedValue()
        => ErrorCodes.ElevationDenied.Should().Be("ELEVATION_DENIED");

    [Fact]
    public void ErrorCodes_SignatureInvalid_HasExpectedValue()
        => ErrorCodes.SignatureInvalid.Should().Be("SIGNATURE_INVALID");

    [Fact]
    public void ErrorCodes_ManifestTampered_HasExpectedValue()
        => ErrorCodes.ManifestTampered.Should().Be("MANIFEST_TAMPERED");

    [Fact]
    public void ErrorCodes_GuideLinkTampered_HasExpectedValue()
        => ErrorCodes.GuideLinkTampered.Should().Be("GUIDE_LINK_TAMPERED");

    [Fact]
    public void ErrorCodes_UpdateExpired_HasExpectedValue()
        => ErrorCodes.UpdateExpired.Should().Be("UPDATE_EXPIRED");

    [Fact]
    public void ErrorCodes_OpNotAllowed_HasExpectedValue()
        => ErrorCodes.OpNotAllowed.Should().Be("OP_NOT_ALLOWED");

    [Fact]
    public void ErrorCodes_IoFailure_HasExpectedValue()
        => ErrorCodes.IoFailure.Should().Be("IO_FAILURE");
}
