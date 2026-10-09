using System.Runtime.Versioning;

namespace DrDxt.AmdHarness;

// -----------------------------------------------------------------------
// RealHarnessCleanupVerifier — Task 5a.
//
// Inspects the machine footprint after a harness session and reports
// HONESTLY what it finds.  CARDINAL INVARIANT: never throws; inspection
// failures are surfaced as residuals, never silently swallowed.
//
// What it checks:
//   (1) SCM — is "DrDxtReconciler" (the product Windows service) installed?
//       The harness never installs this; finding it means the product
//       installer previously ran on this machine.
//   (2) Task Scheduler — any task whose name contains "DrDxt"?
//       Same reasoning: unexpected on a harness-only box.
//   (3) Residual files / dirs — the harness is service-less and leaves NO
//       temp files. KnownHarnessTempPaths is intentionally empty; the check
//       is present for structural completeness and honest extension points
//       (add paths here if a future harness step writes to disk).
//
// NoServiceInstalled = true   ONLY when the SCM query succeeded AND found nothing.
// NoServiceInstalled = false  when the service was found OR the query failed
//                             (cannot positively confirm absence — honest).
// Same rule applies for NoScheduledTaskInstalled.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class RealHarnessCleanupVerifier : IHarnessCleanupVerifier
{
    /// <summary>The Windows service name registered by the DrDXT product installer
    /// (matches AddWindowsService ServiceName in the reconciler host).</summary>
    internal const string ReconcilerServiceName = "DrDxtReconciler";

    /// <summary>Substring matched against Task Scheduler task names to detect
    /// any DrDXT-related task registered by the product installer.</summary>
    internal const string DrDxtTaskSubstring = "DrDxt";

    /// <summary>Known harness temp/work paths to verify are absent after a session.
    /// The harness is service-less and creates no temporary files, so this list is
    /// intentionally empty. Add paths here if a future harness step writes to disk.</summary>
    internal static readonly IReadOnlyList<string> KnownHarnessTempPaths =
        Array.Empty<string>();

    private readonly IServiceInspector _services;
    private readonly ITaskSchedulerInspector _tasks;
    private readonly IResidualPathInspector _residuals;

    internal RealHarnessCleanupVerifier(
        IServiceInspector services,
        ITaskSchedulerInspector tasks,
        IResidualPathInspector residuals)
    {
        _services  = services;
        _tasks     = tasks;
        _residuals = residuals;
    }

    public Task<CleanupVerifyReport> VerifyAsync(CancellationToken ct)
    {
        var found   = new List<string>();
        bool noSvc  = true;
        bool noTask = true;

        // ── (1) SCM: DrDxtReconciler service ─────────────────────────────────────
        try
        {
            var r = _services.IsInstalled(ReconcilerServiceName);
            if (!r.IsOk)
            {
                found.Add($"service-inspect-error: {r.Error?.Message ?? "unknown"}");
                noSvc = false;   // cannot positively confirm absence
            }
            else if (r.Value)
            {
                found.Add($"service '{ReconcilerServiceName}' still installed");
                noSvc = false;
            }
        }
        catch (Exception ex)
        {
            found.Add($"service-inspect-threw: {ex.GetType().Name}: {ex.Message}");
            noSvc = false;
        }

        // ── (2) Task Scheduler: any "DrDxt*" task ────────────────────────────────
        try
        {
            var r = _tasks.FindTasksMatching(DrDxtTaskSubstring);
            if (!r.IsOk)
            {
                found.Add($"task-inspect-error: {r.Error?.Message ?? "unknown"}");
                noTask = false;
            }
            else if (r.Value!.Count > 0)
            {
                foreach (var t in r.Value)
                    found.Add($"scheduled task '{t}' found");
                noTask = false;
            }
        }
        catch (Exception ex)
        {
            found.Add($"task-inspect-threw: {ex.GetType().Name}: {ex.Message}");
            noTask = false;
        }

        // ── (3) Residual files / directories ─────────────────────────────────────
        try
        {
            foreach (var p in _residuals.FindExisting(KnownHarnessTempPaths))
                found.Add($"residual path: {p}");
        }
        catch (Exception ex)
        {
            found.Add($"path-inspect-threw: {ex.GetType().Name}: {ex.Message}");
        }

        return Task.FromResult(new CleanupVerifyReport(
            NoServiceInstalled:       noSvc,
            NoScheduledTaskInstalled: noTask,
            ResidualsFound:           found));
    }
}
