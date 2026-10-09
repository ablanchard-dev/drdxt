using DrDxt.AmdHarness.Reporting;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;

namespace DrDxt.AmdHarness.Tests;

// -----------------------------------------------------------------------
// HarnessReportRenderer TDD tests (Task 6).
//
// Pure-renderer tests only — no file I/O (WriteAsync not exercised here;
// the renderers return strings, I/O is a thin shell).
//
// Key honesty invariants asserted:
//   - AMD gate Skipped  → "not exercised" present; "Passed" NOT claimed.
//   - NOT clean footprint with residuals → listed explicitly.
//   - Null Hardware     → "detection failed" / "unavailable"; no throw.
//   - Null ScanResult   → "not performed"; no throw.
//   - Null CleanupReport → "could not confirm"; no throw.
//   - Gate Passed       → GPU steps surfaced with OK / FAIL markers.
//   - RevertCompleted=false → rendered honestly (not hidden as OK).
// -----------------------------------------------------------------------
public sealed class HarnessReportRendererTests
{
    // ── Shared test data ──────────────────────────────────────────────────────

    private static readonly HardwareInventory AmdHw = new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo("AMD Radeon RX 7800 XT", "AMD", 16L << 30, "23.9.1", TriState.On) },
        new[] { new MemoryModule("DDR5-6000", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "ROG CROSSHAIR X670E"),
        new BiosInfo("2.10.1", new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero), IsUefi: true),
        ChassisType.Desktop);

    private static readonly SettingsState SomeState = new(
        XmpExpo:            TriState.On,
        ReBar:              TriState.On,
        PowerPlanGuid:      "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        Hags:               TriState.On,
        GameMode:           TriState.On,
        ParkedCoreIndexes:  Array.Empty<int>(),
        Vrr:                TriState.On);

    private static readonly ScoreBreakdown SomeBreakdown = new(Performance: 82, Latency: 75, Stability: 90);

    private static readonly DimensionStatus PartialDims = new(
        LatencyMeasured: false, StabilityFullyAssessed: false);

    private static ScanResult MakeScanResult(IReadOnlyList<Finding>? findings = null) =>
        new(AmdHw, SomeState,
            findings ?? Array.Empty<Finding>(),
            Score: 78,
            Baseline: null,
            SomeBreakdown,
            PartialDims);

    private static readonly CleanupVerifyReport CleanReport = new(
        NoServiceInstalled: true,
        NoScheduledTaskInstalled: true,
        ResidualsFound: Array.Empty<string>());

    // A "full" report with gate Passed and all GPU steps present.
    private static HarnessReport MakeFullPassedReport(
        HardwareInventory? hw        = null,
        ScanResult?        scan      = null,
        GpuGateOutcome     gate      = GpuGateOutcome.Passed,
        CleanupVerifyReport? cleanup = null,
        bool revertCompleted         = true) =>
        new(IsCompleted:     true,
            Hardware:        hw   ?? AmdHw,
            ScanResult:      scan ?? MakeScanResult(),
            GpuGate:         gate,
            StepOutcomes:    MakeFullStepList(gate),
            RevertCompleted: revertCompleted,
            CleanupReport:   cleanup ?? CleanReport);

    private static IReadOnlyList<HarnessStepOutcome> MakeFullStepList(GpuGateOutcome gate)
    {
        var steps = new List<HarnessStepOutcome>
        {
            new("detect",     Succeeded: true),
            new("read-state", Succeeded: true),
            new("scan",       Succeeded: true),
            new("safe-apply", Succeeded: false,
                "SAFE_TWEAKS_STUB: not exercised by this harness"),
        };

        if (gate == GpuGateOutcome.Passed)
        {
            steps.Add(new("adlx-gate",         Succeeded: true,  "ADLX read ok (current undervolt value = 50)."));
            steps.Add(new("micro-write",        Succeeded: true,  "SET MaxFreq offset=-15 MHz — SET path confirmed."));
            steps.Add(new("micro-write-revert", Succeeded: true));
            steps.Add(new("bench-before-perf",  Succeeded: true,  "avgFps=145.2 p1Low=112.4"));
            steps.Add(new("perf-write",         Succeeded: true,  "SET MaxFreq offset=+50 MHz."));
            steps.Add(new("bench-after-perf",   Succeeded: true,  "avgFps=149.7 p1Low=115.1 (delta avgFps=+4.5)"));
            steps.Add(new("perf-write-revert",  Succeeded: true));
        }
        else
        {
            steps.Add(new("adlx-gate", Succeeded: false,
                "ADLX SDK not available on this machine — GPU write not validated."));
        }

        steps.Add(new("cleanup-verify", Succeeded: true));
        return steps;
    }

    // ── RenderText: machine section ───────────────────────────────────────────

    [Fact]
    public void RenderText_ContainsCpuModel()
    {
        var report = MakeFullPassedReport();
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("Ryzen 7 7800X3D");
    }

    [Fact]
    public void RenderText_ContainsBoardAndBios()
    {
        var report = MakeFullPassedReport();
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("ROG CROSSHAIR X670E");
        txt.Should().Contain("2.10.1");
    }

    [Fact]
    public void RenderText_ContainsGpuModel()
    {
        var report = MakeFullPassedReport();
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("AMD Radeon RX 7800 XT");
    }

    // ── RenderText: scan section ──────────────────────────────────────────────

    [Fact]
    public void RenderText_ContainsScanScore()
    {
        var report = MakeFullPassedReport();
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("78 / 100");
    }

    [Fact]
    public void RenderText_ContainsBreakdownValues()
    {
        var report = MakeFullPassedReport();
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("Performance 82");
        txt.Should().Contain("Latency 75");
        txt.Should().Contain("Stability 90");
    }

    [Fact]
    public void RenderText_HonestyFlags_LatencyNotMeasured()
    {
        var report = MakeFullPassedReport();
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("NOT done (no PresentMon baseline)");
    }

    // ── RenderText: GPU gate SKIPPED — the core honesty invariant ─────────────

    [Fact]
    public void RenderText_GpuGateSkipped_ContainsNotExercisedLine()
    {
        var report = MakeFullPassedReport(gate: GpuGateOutcome.Skipped);
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("AMD GPU tuning not exercised on this machine");
    }

    // ── Diagnostic probe: a skipped gate shows EXACTLY where ADLX stopped ─────

    private static HarnessReport MakeSkippedReportWithProbe() =>
        new(IsCompleted:     true,
            Hardware:        AmdHw,
            ScanResult:      MakeScanResult(),
            GpuGate:         GpuGateOutcome.Skipped,
            StepOutcomes:    MakeFullStepList(GpuGateOutcome.Skipped),
            RevertCompleted: true,
            CleanupReport:   CleanReport,
            GpuProbe: new GpuProbeStep[]
            {
                new("adlx-init",            true,  "ADLXInitialize ok (result=0)."),
                new("manual-gfx-supported", false, "Card reports manual GFX tuning UNSUPPORTED for this GPU/driver."),
            });

    [Fact]
    public void RenderText_GpuGateSkipped_RendersDiagnosticProbe()
    {
        var txt = HarnessReportRenderer.RenderText(MakeSkippedReportWithProbe());
        txt.Should().Contain("Diagnostic");
        txt.Should().Contain("manual-gfx-supported");
        txt.Should().Contain("UNSUPPORTED");
    }

    [Fact]
    public void RenderHtml_GpuGateSkipped_RendersDiagnosticProbe()
    {
        var html = HarnessReportRenderer.RenderHtml(MakeSkippedReportWithProbe());
        html.Should().Contain("Diagnostic");
        html.Should().Contain("manual-gfx-supported");
    }

    [Fact]
    public void RenderText_GpuGateSkipped_DoesNotClaimPassed()
    {
        var report = MakeFullPassedReport(gate: GpuGateOutcome.Skipped);
        var txt    = HarnessReportRenderer.RenderText(report);
        // Must NOT contain the "Passed — AMD GPU write" line that appears when the gate passed.
        txt.Should().NotContain("Passed — AMD GPU write path exercised");
    }

    [Fact]
    public void RenderText_GpuGateSkipped_ShowsSkippedGateReason()
    {
        var report = MakeFullPassedReport(gate: GpuGateOutcome.Skipped);
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("ADLX SDK not available on this machine");
    }

    // ── RenderText: GPU gate PASSED — GPU steps surfaced ─────────────────────

    [Fact]
    public void RenderText_GpuGatePassed_ContainsGpuStepOutcomes()
    {
        var report = MakeFullPassedReport(gate: GpuGateOutcome.Passed);
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("micro-write");
        txt.Should().Contain("perf-write");
        txt.Should().Contain("+4.5");  // delta from bench-after-perf note
    }

    [Fact]
    public void RenderText_GpuGatePassed_ContainsPerfWriteRevert()
    {
        var report = MakeFullPassedReport(gate: GpuGateOutcome.Passed);
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("perf-write-revert");
    }

    // ── RenderText: NOT clean footprint ───────────────────────────────────────

    [Fact]
    public void RenderText_NotCleanFootprint_SaysNotClean()
    {
        var dirtyCleanup = new CleanupVerifyReport(
            NoServiceInstalled:      false,
            NoScheduledTaskInstalled: true,
            ResidualsFound:          new[] { @"C:\ProgramData\DrDXT\leftover.dat" });

        var report = MakeFullPassedReport(cleanup: dirtyCleanup);
        var txt    = HarnessReportRenderer.RenderText(report);

        txt.Should().Contain("NOT CLEAN");
    }

    [Fact]
    public void RenderText_NotCleanFootprint_ListsResiduals()
    {
        var dirtyCleanup = new CleanupVerifyReport(
            NoServiceInstalled:      true,
            NoScheduledTaskInstalled: true,
            ResidualsFound:          new[] { @"C:\ProgramData\DrDXT\leftover.dat", @"C:\Temp\drdxt-tmp" });

        var report = MakeFullPassedReport(cleanup: dirtyCleanup);
        var txt    = HarnessReportRenderer.RenderText(report);

        txt.Should().Contain(@"C:\ProgramData\DrDXT\leftover.dat");
        txt.Should().Contain(@"C:\Temp\drdxt-tmp");
    }

    [Fact]
    public void RenderText_NotCleanFootprint_ServiceFound_SaysServiceFound()
    {
        var dirtyCleanup = new CleanupVerifyReport(
            NoServiceInstalled:      false,
            NoScheduledTaskInstalled: true,
            ResidualsFound:          Array.Empty<string>());

        var report = MakeFullPassedReport(cleanup: dirtyCleanup);
        var txt    = HarnessReportRenderer.RenderText(report);

        txt.Should().Contain("SERVICE FOUND");
    }

    // ── RenderText: null fields — never throw ─────────────────────────────────

    [Fact]
    public void RenderText_NullHardware_SaysDetectionFailed_DoesNotThrow()
    {
        var report = new HarnessReport(
            IsCompleted:     true,
            Hardware:        null,
            ScanResult:      null,
            GpuGate:         GpuGateOutcome.Skipped,
            StepOutcomes:    new[] { new HarnessStepOutcome("detect", Succeeded: false, "WMI error") },
            RevertCompleted: true,
            CleanupReport:   CleanReport);

        var act = () => HarnessReportRenderer.RenderText(report);
        act.Should().NotThrow();

        var txt = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("detection failed");
        // Must NOT contain the null-reference sentinel string.
        txt.Should().NotContain("Object reference");
    }

    [Fact]
    public void RenderText_NullScanResult_SaysScanNotPerformed_DoesNotThrow()
    {
        var report = new HarnessReport(
            IsCompleted:     true,
            Hardware:        AmdHw,
            ScanResult:      null,
            GpuGate:         GpuGateOutcome.Skipped,
            StepOutcomes:    Array.Empty<HarnessStepOutcome>(),
            RevertCompleted: true,
            CleanupReport:   CleanReport);

        var act = () => HarnessReportRenderer.RenderText(report);
        act.Should().NotThrow();

        var txt = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("Scan not performed");
    }

    [Fact]
    public void RenderText_NullCleanupReport_SaysVerifierCouldNotConfirm_DoesNotThrow()
    {
        var report = new HarnessReport(
            IsCompleted:     true,
            Hardware:        AmdHw,
            ScanResult:      MakeScanResult(),
            GpuGate:         GpuGateOutcome.Skipped,
            StepOutcomes:    Array.Empty<HarnessStepOutcome>(),
            RevertCompleted: true,
            CleanupReport:   null);

        var act = () => HarnessReportRenderer.RenderText(report);
        act.Should().NotThrow();

        var txt = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("could not confirm");
    }

    // ── RenderText: revert not completed ─────────────────────────────────────

    [Fact]
    public void RenderText_RevertNotCompleted_SaysNoHonestly()
    {
        var report = MakeFullPassedReport(revertCompleted: false);
        var txt    = HarnessReportRenderer.RenderText(report);
        txt.Should().Contain("NO — revert loop did not finish");
    }

    // ── RenderHtml: AMD gate SKIPPED — core honesty invariant ────────────────

    [Fact]
    public void RenderHtml_GpuGateSkipped_ContainsNotExercisedLine()
    {
        var report = MakeFullPassedReport(gate: GpuGateOutcome.Skipped);
        var html   = HarnessReportRenderer.RenderHtml(report);
        html.Should().Contain("AMD GPU tuning not exercised on this machine");
    }

    [Fact]
    public void RenderHtml_GpuGateSkipped_HasSkippedTag_NotPassedTag()
    {
        var report = MakeFullPassedReport(gate: GpuGateOutcome.Skipped);
        var html   = HarnessReportRenderer.RenderHtml(report);
        // The "Skipped" CSS tag must appear in the GPU section.
        html.Should().Contain("class=\"tag skipped\"");
        // The "Passed" prose must NOT appear in the GPU block.
        html.Should().NotContain("AMD GPU write path exercised");
    }

    // ── RenderHtml: NOT clean footprint ───────────────────────────────────────

    [Fact]
    public void RenderHtml_NotCleanFootprint_ShowsNotCleanTag()
    {
        var dirtyCleanup = new CleanupVerifyReport(
            NoServiceInstalled:      false,
            NoScheduledTaskInstalled: false,
            ResidualsFound:          new[] { @"C:\ProgramData\DrDXT\leftover.dat" });

        var report = MakeFullPassedReport(cleanup: dirtyCleanup);
        var html   = HarnessReportRenderer.RenderHtml(report);

        html.Should().Contain("notclean");
        html.Should().Contain("NOT CLEAN");
    }

    [Fact]
    public void RenderHtml_NotCleanFootprint_ListsResiduals()
    {
        var dirtyCleanup = new CleanupVerifyReport(
            NoServiceInstalled:      true,
            NoScheduledTaskInstalled: true,
            ResidualsFound:          new[] { @"C:\ProgramData\DrDXT\leftover.dat" });

        var report = MakeFullPassedReport(cleanup: dirtyCleanup);
        var html   = HarnessReportRenderer.RenderHtml(report);

        html.Should().Contain("leftover.dat");
    }

    // ── RenderHtml: null fields — never throw ─────────────────────────────────

    [Fact]
    public void RenderHtml_NullHardware_SaysDetectionFailed_DoesNotThrow()
    {
        var report = new HarnessReport(
            IsCompleted:     true,
            Hardware:        null,
            ScanResult:      null,
            GpuGate:         GpuGateOutcome.Skipped,
            StepOutcomes:    Array.Empty<HarnessStepOutcome>(),
            RevertCompleted: true,
            CleanupReport:   null);

        var act = () => HarnessReportRenderer.RenderHtml(report);
        act.Should().NotThrow();

        var html = HarnessReportRenderer.RenderHtml(report);
        html.Should().Contain("detection failed");
    }

    [Fact]
    public void RenderHtml_NullCleanupReport_SaysVerifierCouldNotConfirm_DoesNotThrow()
    {
        var report = new HarnessReport(
            IsCompleted:     true,
            Hardware:        AmdHw,
            ScanResult:      null,
            GpuGate:         GpuGateOutcome.Skipped,
            StepOutcomes:    Array.Empty<HarnessStepOutcome>(),
            RevertCompleted: true,
            CleanupReport:   null);

        var act = () => HarnessReportRenderer.RenderHtml(report);
        act.Should().NotThrow();

        var html = HarnessReportRenderer.RenderHtml(report);
        html.Should().Contain("could not confirm");
    }

    // ── RenderHtml: clean report is honest too ────────────────────────────────

    [Fact]
    public void RenderHtml_CleanFootprint_ShowsCleanTag()
    {
        var report = MakeFullPassedReport(cleanup: CleanReport);
        var html   = HarnessReportRenderer.RenderHtml(report);
        html.Should().Contain("class=\"tag clean\"");
        html.Should().Contain(">CLEAN<");
    }

    // ── RenderHtml: findings rendered ────────────────────────────────────────

    [Fact]
    public void RenderHtml_ScanFindings_ArePresent()
    {
        var findings = new[]
        {
            new Finding("Power", Severity.Major, "power.plan.not.optimal", EstimatedGainPct: 8.0),
        };
        var report = MakeFullPassedReport(scan: MakeScanResult(findings));
        var html   = HarnessReportRenderer.RenderHtml(report);

        html.Should().Contain("power.plan.not.optimal");
        html.Should().Contain("+8%");
    }

    // ── RenderHtml: basic structure ───────────────────────────────────────────

    [Fact]
    public void RenderHtml_IsWellFormedHtmlSkeleton()
    {
        var report = MakeFullPassedReport();
        var html   = HarnessReportRenderer.RenderHtml(report);

        html.Should().StartWith("<!DOCTYPE html>");
        html.Should().Contain("<html");
        html.Should().Contain("</html>");
        html.Should().Contain("<body>");
        html.Should().Contain("</body>");
    }
}
