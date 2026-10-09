using System.Text;
using DrDxt.Core;

namespace DrDxt.AmdHarness.Reporting;

// -----------------------------------------------------------------------
// HarnessReportRenderer — pure renderer for HarnessReport (Task 6).
//
// RenderText / RenderHtml are pure functions: no I/O, no side effects,
// never throw (all exceptions caught and reported as an error string).
// WriteAsync is the thin I/O shell that materialises both files.
//
// Honesty rules (the product's core value):
//   - Skipped / failed / unmeasured steps are shown as such.
//   - A Skipped GPU gate renders "not exercised" — never implies success.
//   - Null fields render as "unavailable" — never throw.
//   - A NOT-clean footprint lists every residual explicitly.
//   - The cleanup verifier failing to run is surfaced, never hidden.
// -----------------------------------------------------------------------

/// <summary>
/// Pure renderer — no I/O. <see cref="RenderText"/> and <see cref="RenderHtml"/>
/// return strings and never throw. <see cref="WriteAsync"/> is the only I/O path.
/// </summary>
public static class HarnessReportRenderer
{
    // Step IDs that belong to the AMD GPU write sequence (step 5).
    private static readonly HashSet<string> GpuStepIds = new(StringComparer.Ordinal)
    {
        "micro-write", "micro-write-revert",
        "bench-before-perf",
        "perf-write", "bench-after-perf", "perf-write-revert",
    };

    // Ordered list for rendering the GPU section in execution order.
    private static readonly string[] GpuStepOrder =
    {
        "micro-write", "micro-write-revert",
        "bench-before-perf",
        "perf-write", "bench-after-perf", "perf-write-revert",
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders a sober plain-text report from <paramref name="report"/>.
    /// Never throws — returns an error line on internal fault.
    /// </summary>
    public static string RenderText(HarnessReport report)
    {
        try
        {
            var sb  = new StringBuilder();
            var now = DateTimeOffset.UtcNow;

            TxtLine(sb, "DrDXT AMD Harness — Run Report");
            TxtLine(sb, $"Generated : {now:yyyy-MM-dd HH:mm:ss} UTC");
            TxtLine(sb, new string('=', 70));
            sb.AppendLine();

            // ── Machine ───────────────────────────────────────────────────────
            TxtSection(sb, "MACHINE");
            if (report.Hardware is null)
            {
                TxtLine(sb, "  Hardware detection failed — machine info unavailable.");
            }
            else
            {
                var hw  = report.Hardware;
                var cpu = hw.Cpu;
                string x3dTag = cpu.IsX3D ? $" [X3D, {cpu.CcdCount} CCD]" : string.Empty;
                TxtLine(sb, $"  CPU     : {cpu.Model} ({cpu.Vendor}) — {cpu.Cores} cores / {cpu.Threads} threads{x3dTag}");

                if (hw.Gpus.Count == 0)
                {
                    TxtLine(sb, "  GPU     : none detected");
                }
                else
                {
                    foreach (var gpu in hw.Gpus)
                    {
                        long vramMb = gpu.VramBytes / (1024L * 1024L);
                        TxtLine(sb, $"  GPU     : {gpu.Model} ({gpu.Vendor}) — {vramMb} MB VRAM, driver {gpu.DriverVersion}, ReBar {gpu.ReBar}");
                    }
                }

                if (hw.Memory.Count == 0)
                {
                    TxtLine(sb, "  RAM     : none detected");
                }
                else
                {
                    var m       = hw.Memory[0];
                    string desc = hw.Memory.Count == 1
                        ? $"{m.Manufacturer} {m.PartNumber} — {m.ConfiguredMhz} MHz (rated {m.RatedMhz} MHz)"
                        : $"{hw.Memory.Count}x modules — first: {m.Manufacturer} {m.PartNumber} {m.ConfiguredMhz} MHz";
                    TxtLine(sb, $"  RAM     : {desc}");
                }

                TxtLine(sb, $"  Board   : {hw.Board.Manufacturer} {hw.Board.Product}");
                string biosDate = hw.Bios.ReleaseDate.HasValue
                    ? hw.Bios.ReleaseDate.Value.ToString("yyyy-MM-dd")
                    : "date unknown";
                string uefiTag = hw.Bios.IsUefi ? ", UEFI" : string.Empty;
                TxtLine(sb, $"  BIOS    : {hw.Bios.Version} ({biosDate}){uefiTag}");
                TxtLine(sb, $"  Chassis : {hw.Chassis}");
            }
            sb.AppendLine();

            // ── Scan ──────────────────────────────────────────────────────────
            TxtSection(sb, "SCAN");
            if (report.ScanResult is null)
            {
                TxtLine(sb, "  Scan not performed or failed — no score available.");
            }
            else
            {
                var scan = report.ScanResult;
                TxtLine(sb, $"  Score     : {scan.Score} / 100");
                TxtLine(sb, $"  Breakdown : Performance {scan.Breakdown.Performance} / Latency {scan.Breakdown.Latency} / Stability {scan.Breakdown.Stability}");

                if (scan.Findings.Count == 0)
                {
                    TxtLine(sb, "  Findings  : none");
                }
                else
                {
                    TxtLine(sb, $"  Findings ({scan.Findings.Count}):");
                    foreach (var f in scan.Findings)
                    {
                        string gain = f.EstimatedGainPct.HasValue ? $" — est. +{f.EstimatedGainPct:F0}% gain" : string.Empty;
                        TxtLine(sb, $"    [{f.Severity}] {f.Category}: {f.TitleKey}{gain}");
                    }
                }

                TxtLine(sb, "  Honesty flags:");
                TxtLine(sb, $"    Latency measurement  : {(scan.DimensionStatus.LatencyMeasured ? "done" : "NOT done (no PresentMon baseline)")}");
                TxtLine(sb, $"    Stability assessment : {(scan.DimensionStatus.StabilityFullyAssessed ? "full audit done" : "limited (not a full audit)")}");
            }
            sb.AppendLine();

            // ── GPU Tuning ────────────────────────────────────────────────────
            TxtSection(sb, "GPU TUNING (AMD / ADLX)");
            if (report.GpuGate == GpuGateOutcome.Skipped)
            {
                TxtLine(sb, "  Gate   : Skipped");
                var gateStep = report.StepOutcomes.FirstOrDefault(s => s.StepId == "adlx-gate");
                if (gateStep?.Note is not null)
                    TxtLine(sb, $"  Reason : {gateStep.Note}");
                TxtLine(sb, "  AMD GPU tuning not exercised on this machine.");
                if (report.GpuProbe is { Count: > 0 })
                {
                    TxtLine(sb, "  Diagnostic (ADLX read path — exactly where it stopped):");
                    foreach (var p in report.GpuProbe)
                        TxtLine(sb, $"    [{(p.Ok ? "OK" : "!!")}] {p.Name}{(p.Detail is null ? string.Empty : $" — {p.Detail}")}");
                }
            }
            else
            {
                TxtLine(sb, "  Gate   : Passed — AMD GPU write path exercised");
                foreach (var stepId in GpuStepOrder)
                {
                    var step = report.StepOutcomes.FirstOrDefault(s => s.StepId == stepId);
                    if (step is not null)
                        TxtLine(sb, $"  [{(step.Succeeded ? "OK" : "!!")}] {step.StepId}{(step.Note is null ? string.Empty : $" — {step.Note}")}");
                }
            }
            sb.AppendLine();

            // ── All Steps ─────────────────────────────────────────────────────
            TxtSection(sb, "STEPS (all)");
            if (report.StepOutcomes.Count == 0)
            {
                TxtLine(sb, "  (no steps recorded)");
            }
            else
            {
                foreach (var step in report.StepOutcomes)
                    TxtLine(sb, $"  [{(step.Succeeded ? "OK" : "!!")}] {step.StepId}{(step.Note is null ? string.Empty : $" — {step.Note}")}");
            }
            sb.AppendLine();

            // ── Revert ────────────────────────────────────────────────────────
            TxtSection(sb, "REVERT");
            TxtLine(sb, report.RevertCompleted
                ? "  Completed : Yes"
                : "  Completed : NO — revert loop did not finish (see steps above)");
            sb.AppendLine();

            // ── Footprint ─────────────────────────────────────────────────────
            TxtSection(sb, "FOOTPRINT");
            if (report.CleanupReport is null)
            {
                TxtLine(sb, "  The cleanup verifier could not confirm the footprint — check manually.");
            }
            else
            {
                var cr      = report.CleanupReport;
                bool clean  = cr.NoServiceInstalled && cr.NoScheduledTaskInstalled && cr.ResidualsFound.Count == 0;

                TxtLine(sb, $"  Windows service : {(cr.NoServiceInstalled ? "none installed" : "SERVICE FOUND — not clean")}");
                TxtLine(sb, $"  Scheduled task  : {(cr.NoScheduledTaskInstalled ? "none installed" : "TASK FOUND — not clean")}");

                if (cr.ResidualsFound.Count == 0)
                {
                    TxtLine(sb, "  Residuals       : none");
                }
                else
                {
                    TxtLine(sb, $"  Residuals ({cr.ResidualsFound.Count}):");
                    foreach (var r in cr.ResidualsFound)
                        TxtLine(sb, $"    - {r}");
                }

                TxtLine(sb, $"  Status          : {(clean ? "CLEAN" : "NOT CLEAN")}");
            }
            sb.AppendLine();

            TxtLine(sb, new string('-', 70));
            TxtLine(sb, "End of report.");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"[RenderText internal error: {ex.GetType().Name}: {ex.Message}]{Environment.NewLine}";
        }
    }

    /// <summary>
    /// Renders a clean HTML report from <paramref name="report"/>.
    /// No external frameworks — inline styles only.
    /// Never throws — returns an HTML comment on internal fault.
    /// </summary>
    public static string RenderHtml(HarnessReport report)
    {
        try
        {
            var sb  = new StringBuilder();
            var now = DateTimeOffset.UtcNow;

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\" />");
            sb.AppendLine("<title>DrDXT AMD Harness — Run Report</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:Consolas,'Courier New',monospace;font-size:13px;background:#f4f4f4;color:#111;margin:28px 36px;}");
            sb.AppendLine("h1{font-size:1.15em;border-bottom:2px solid #333;padding-bottom:4px;margin-bottom:4px;}");
            sb.AppendLine("h2{font-size:1em;margin-top:22px;margin-bottom:4px;color:#222;border-left:4px solid #555;padding-left:8px;}");
            sb.AppendLine("p.note{margin:3px 0;color:#444;}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;margin-top:4px;}");
            sb.AppendLine("th{background:#2e2e2e;color:#f0f0f0;text-align:left;padding:4px 10px;font-weight:normal;}");
            sb.AppendLine("td{padding:3px 10px;border-bottom:1px solid #ddd;vertical-align:top;}");
            sb.AppendLine("tr:nth-child(even)>td{background:#ebebeb;}");
            sb.AppendLine(".ok{color:#1a6e1a;font-weight:bold;}");
            sb.AppendLine(".fail{color:#9e0000;font-weight:bold;}");
            sb.AppendLine(".warn{color:#7a4d00;}");
            sb.AppendLine(".tag{display:inline-block;padding:1px 7px;border-radius:3px;font-size:11px;font-weight:bold;}");
            sb.AppendLine(".clean{background:#d4edda;color:#155724;}");
            sb.AppendLine(".notclean{background:#f8d7da;color:#721c24;}");
            sb.AppendLine(".skipped{background:#fff3cd;color:#7a4d00;}");
            sb.AppendLine("hr{border:0;border-top:1px solid #ccc;margin-top:24px;}");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            sb.AppendLine("<h1>DrDXT AMD Harness &mdash; Run Report</h1>");
            sb.AppendLine($"<p class=\"note\">Generated: {HE(now.ToString("yyyy-MM-dd HH:mm:ss"))} UTC</p>");

            // ── Machine ───────────────────────────────────────────────────────
            sb.AppendLine("<h2>Machine</h2>");
            if (report.Hardware is null)
            {
                sb.AppendLine("<p class=\"note warn\">Hardware detection failed &mdash; machine info unavailable.</p>");
            }
            else
            {
                var hw = report.Hardware;
                sb.AppendLine("<table><tr><th>Component</th><th>Details</th></tr>");

                var cpu = hw.Cpu;
                string x3dTag = cpu.IsX3D ? $" [X3D, {cpu.CcdCount} CCD]" : string.Empty;
                HtmlTableRow(sb, "CPU",
                    $"{HE(cpu.Model)} ({HE(cpu.Vendor)}) &mdash; {cpu.Cores} cores / {cpu.Threads} threads{HE(x3dTag)}");

                if (hw.Gpus.Count == 0)
                {
                    HtmlTableRow(sb, "GPU", "none detected");
                }
                else
                {
                    foreach (var gpu in hw.Gpus)
                    {
                        long vramMb = gpu.VramBytes / (1024L * 1024L);
                        HtmlTableRow(sb, "GPU",
                            $"{HE(gpu.Model)} ({HE(gpu.Vendor)}) &mdash; {vramMb} MB VRAM, driver {HE(gpu.DriverVersion)}, ReBar {gpu.ReBar}");
                    }
                }

                if (hw.Memory.Count == 0)
                {
                    HtmlTableRow(sb, "RAM", "none detected");
                }
                else
                {
                    var m       = hw.Memory[0];
                    string desc = hw.Memory.Count == 1
                        ? $"{HE(m.Manufacturer)} {HE(m.PartNumber)} &mdash; {m.ConfiguredMhz} MHz (rated {m.RatedMhz} MHz)"
                        : $"{hw.Memory.Count}x modules &mdash; first: {HE(m.Manufacturer)} {HE(m.PartNumber)} {m.ConfiguredMhz} MHz";
                    HtmlTableRow(sb, "RAM", desc);
                }

                HtmlTableRow(sb, "Board", $"{HE(hw.Board.Manufacturer)} {HE(hw.Board.Product)}");
                string biosDate = hw.Bios.ReleaseDate.HasValue
                    ? hw.Bios.ReleaseDate.Value.ToString("yyyy-MM-dd")
                    : "date unknown";
                string uefiTag = hw.Bios.IsUefi ? ", UEFI" : string.Empty;
                HtmlTableRow(sb, "BIOS", $"{HE(hw.Bios.Version)} ({HE(biosDate)}){HE(uefiTag)}");
                HtmlTableRow(sb, "Chassis", HE(hw.Chassis.ToString()));

                sb.AppendLine("</table>");
            }

            // ── Scan ──────────────────────────────────────────────────────────
            sb.AppendLine("<h2>Scan</h2>");
            if (report.ScanResult is null)
            {
                sb.AppendLine("<p class=\"note warn\">Scan not performed or failed &mdash; no score available.</p>");
            }
            else
            {
                var scan = report.ScanResult;
                sb.AppendLine("<table><tr><th>Metric</th><th>Value</th></tr>");
                HtmlTableRow(sb, "Score",         $"{scan.Score} / 100");
                HtmlTableRow(sb, "Performance",   scan.Breakdown.Performance.ToString());
                HtmlTableRow(sb, "Latency",       scan.Breakdown.Latency.ToString());
                HtmlTableRow(sb, "Stability",     scan.Breakdown.Stability.ToString());
                HtmlTableRow(sb, "Latency measurement",
                    scan.DimensionStatus.LatencyMeasured
                        ? "done"
                        : "<span class=\"warn\">NOT done (no PresentMon baseline)</span>");
                HtmlTableRow(sb, "Stability assessment",
                    scan.DimensionStatus.StabilityFullyAssessed
                        ? "full audit done"
                        : "<span class=\"warn\">limited (not a full audit)</span>");
                sb.AppendLine("</table>");

                if (scan.Findings.Count > 0)
                {
                    sb.AppendLine("<table style=\"margin-top:6px;\"><tr><th>Severity</th><th>Category</th><th>Finding</th><th>Est. gain</th></tr>");
                    foreach (var f in scan.Findings)
                    {
                        string gainCell = f.EstimatedGainPct.HasValue ? $"+{f.EstimatedGainPct:F0}%" : "&mdash;";
                        sb.AppendLine($"<tr><td>{HE(f.Severity.ToString())}</td><td>{HE(f.Category)}</td><td>{HE(f.TitleKey)}</td><td>{gainCell}</td></tr>");
                    }
                    sb.AppendLine("</table>");
                }
                else
                {
                    sb.AppendLine("<p class=\"note\">No findings.</p>");
                }
            }

            // ── GPU Tuning ────────────────────────────────────────────────────
            sb.AppendLine("<h2>GPU Tuning (AMD / ADLX)</h2>");
            if (report.GpuGate == GpuGateOutcome.Skipped)
            {
                sb.AppendLine("<p><span class=\"tag skipped\">Skipped</span></p>");
                var gateStep = report.StepOutcomes.FirstOrDefault(s => s.StepId == "adlx-gate");
                if (gateStep?.Note is not null)
                    sb.AppendLine($"<p class=\"note warn\">{HE(gateStep.Note)}</p>");
                sb.AppendLine("<p class=\"note\"><strong>AMD GPU tuning not exercised on this machine.</strong></p>");
                if (report.GpuProbe is { Count: > 0 })
                {
                    sb.AppendLine("<p class=\"note\">Diagnostic (ADLX read path &mdash; exactly where it stopped):</p>");
                    sb.AppendLine("<table style=\"margin-top:6px;\"><tr><th>Stage</th><th>Result</th><th>Detail</th></tr>");
                    foreach (var p in report.GpuProbe)
                    {
                        string cls  = p.Ok ? "ok" : "fail";
                        string text = p.Ok ? "OK" : "STOP";
                        sb.AppendLine($"<tr><td>{HE(p.Name)}</td><td class=\"{cls}\">{text}</td><td>{HE(p.Detail ?? string.Empty)}</td></tr>");
                    }
                    sb.AppendLine("</table>");
                }
            }
            else
            {
                sb.AppendLine("<p><span class=\"tag clean\">Passed</span> &mdash; AMD GPU write path exercised.</p>");
                var gpuSteps = GpuStepOrder
                    .Select(id => report.StepOutcomes.FirstOrDefault(s => s.StepId == id))
                    .Where(s => s is not null)
                    .ToList();

                if (gpuSteps.Count > 0)
                {
                    sb.AppendLine("<table style=\"margin-top:6px;\"><tr><th>Step</th><th>Result</th><th>Note</th></tr>");
                    foreach (var step in gpuSteps)
                    {
                        string cls  = step!.Succeeded ? "ok" : "fail";
                        string text = step.Succeeded ? "OK" : "FAIL";
                        sb.AppendLine($"<tr><td>{HE(step.StepId)}</td><td class=\"{cls}\">{text}</td><td>{HE(step.Note ?? string.Empty)}</td></tr>");
                    }
                    sb.AppendLine("</table>");
                }
            }

            // ── All Steps ─────────────────────────────────────────────────────
            sb.AppendLine("<h2>All Steps</h2>");
            if (report.StepOutcomes.Count == 0)
            {
                sb.AppendLine("<p class=\"note\">(no steps recorded)</p>");
            }
            else
            {
                sb.AppendLine("<table><tr><th>Step</th><th>Result</th><th>Note</th></tr>");
                foreach (var step in report.StepOutcomes)
                {
                    string cls  = step.Succeeded ? "ok" : "fail";
                    string text = step.Succeeded ? "OK" : "FAIL";
                    sb.AppendLine($"<tr><td>{HE(step.StepId)}</td><td class=\"{cls}\">{text}</td><td>{HE(step.Note ?? string.Empty)}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // ── Revert ────────────────────────────────────────────────────────
            sb.AppendLine("<h2>Revert</h2>");
            if (report.RevertCompleted)
            {
                sb.AppendLine("<p><span class=\"tag clean\">Completed</span></p>");
            }
            else
            {
                sb.AppendLine("<p><span class=\"tag notclean\">DID NOT COMPLETE</span> &mdash; revert loop did not finish; see steps above.</p>");
            }

            // ── Footprint ─────────────────────────────────────────────────────
            sb.AppendLine("<h2>Footprint</h2>");
            if (report.CleanupReport is null)
            {
                sb.AppendLine("<p class=\"note warn\">The cleanup verifier could not confirm the footprint &mdash; check manually.</p>");
            }
            else
            {
                var  cr    = report.CleanupReport;
                bool clean = cr.NoServiceInstalled && cr.NoScheduledTaskInstalled && cr.ResidualsFound.Count == 0;

                string statusClass = clean ? "clean" : "notclean";
                string statusText  = clean ? "CLEAN" : "NOT CLEAN";
                sb.AppendLine($"<p><span class=\"tag {statusClass}\">{statusText}</span></p>");

                sb.AppendLine("<table><tr><th>Check</th><th>Status</th></tr>");
                HtmlTableRow(sb, "Windows service",
                    cr.NoServiceInstalled ? "none installed" : "<span class=\"fail\">SERVICE FOUND</span>");
                HtmlTableRow(sb, "Scheduled task",
                    cr.NoScheduledTaskInstalled ? "none installed" : "<span class=\"fail\">TASK FOUND</span>");

                if (cr.ResidualsFound.Count == 0)
                {
                    HtmlTableRow(sb, "Residuals", "none");
                }
                else
                {
                    string list = string.Join(", ", cr.ResidualsFound.Select(HE));
                    HtmlTableRow(sb, $"Residuals ({cr.ResidualsFound.Count})",
                        $"<span class=\"fail\">{list}</span>");
                }
                sb.AppendLine("</table>");
            }

            sb.AppendLine("<hr />");
            sb.AppendLine("<p class=\"note\">End of report.</p>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"<!-- RenderHtml internal error: {ex.GetType().Name}: {ex.Message} -->{Environment.NewLine}";
        }
    }

    /// <summary>
    /// Writes <c>harness-report-&lt;timestamp&gt;.txt</c> and <c>.html</c> to
    /// <paramref name="directory"/>. Returns the paths of the written files.
    /// </summary>
    public static async Task<(string TxtPath, string HtmlPath)> WriteAsync(
        HarnessReport report, string directory)
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        string stem      = Path.Combine(directory, $"harness-report-{timestamp}");
        string txtPath   = stem + ".txt";
        string htmlPath  = stem + ".html";

        await File.WriteAllTextAsync(txtPath,  RenderText(report), Encoding.UTF8).ConfigureAwait(false);
        await File.WriteAllTextAsync(htmlPath, RenderHtml(report), Encoding.UTF8).ConfigureAwait(false);

        return (txtPath, htmlPath);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void TxtSection(StringBuilder sb, string title)
    {
        sb.AppendLine(title);
        sb.AppendLine(new string('-', title.Length));
    }

    private static void TxtLine(StringBuilder sb, string text) =>
        sb.AppendLine(text);

    private static void HtmlTableRow(StringBuilder sb, string label, string value) =>
        sb.AppendLine($"<tr><td>{HE(label)}</td><td>{value}</td></tr>");

    /// <summary>Minimal HTML-encode — no System.Web dependency.</summary>
    private static string HE(string? s) =>
        s is null ? string.Empty :
        s.Replace("&", "&amp;", StringComparison.Ordinal)
         .Replace("<", "&lt;",  StringComparison.Ordinal)
         .Replace(">", "&gt;",  StringComparison.Ordinal)
         .Replace("\"", "&quot;", StringComparison.Ordinal);
}
