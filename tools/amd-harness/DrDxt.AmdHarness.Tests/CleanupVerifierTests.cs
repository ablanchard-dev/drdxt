using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.AmdHarness.Tests;

// -----------------------------------------------------------------------
// Unit tests for RealHarnessCleanupVerifier (Task 5a).
//
// Fakes of the three seam interfaces (IServiceInspector,
// ITaskSchedulerInspector, IResidualPathInspector) stand in for the real
// Win32 implementations so no OS state is touched during dotnet test.
//
// Invariants covered:
//   (1) Clean machine → fully clean report
//   (2) Service found → NoServiceInstalled=false + name in residuals
//   (3) Task found   → NoScheduledTaskInstalled=false + name in residuals
//   (4) Residual path exists → appears in ResidualsFound
//   (5) Service inspect returns Fail → NoServiceInstalled=false + error surfaced
//   (6) Task inspect returns Fail    → NoScheduledTaskInstalled=false + error surfaced
//   (7) All inspectors throw → VerifyAsync never throws; all errors surfaced honestly
// -----------------------------------------------------------------------
public sealed class CleanupVerifierTests
{
    // ── (1) fully clean ───────────────────────────────────────────────────────

    [Fact]
    public async Task Clean_NoServiceTaskOrResidual_ReturnsFullyCleanReport()
    {
        var report = await Make().VerifyAsync(CancellationToken.None);

        report.NoServiceInstalled.Should().BeTrue("service inspector returned not-found");
        report.NoScheduledTaskInstalled.Should().BeTrue("task inspector returned empty list");
        report.ResidualsFound.Should().BeEmpty("nothing found on a clean machine");
    }

    // ── (2) service found ─────────────────────────────────────────────────────

    [Fact]
    public async Task ServiceFound_NoServiceInstalledIsFalse_ServiceNameInResiduals()
    {
        var svc = new FakeServiceInspector { InspectResult = Result<bool>.Ok(true) };

        var report = await Make(services: svc).VerifyAsync(CancellationToken.None);

        report.NoServiceInstalled.Should().BeFalse();
        report.ResidualsFound.Should().ContainSingle()
            .Which.Should().Contain(RealHarnessCleanupVerifier.ReconcilerServiceName,
                "the known service name must appear in the residuals entry");
    }

    // ── (3) scheduled task found ──────────────────────────────────────────────

    [Fact]
    public async Task TaskFound_NoTaskInstalledIsFalse_TaskNameInResiduals()
    {
        const string taskName = @"\DrDxt\ReconcilerTask";
        var sched = new FakeTaskSchedulerInspector
        {
            InspectResult = Result<IReadOnlyList<string>>.Ok(
                new string[] { taskName })
        };

        var report = await Make(tasks: sched).VerifyAsync(CancellationToken.None);

        report.NoScheduledTaskInstalled.Should().BeFalse();
        report.ResidualsFound.Should().ContainSingle()
            .Which.Should().Contain(taskName, "the task name must appear in the residuals");
    }

    // ── (4) residual path ─────────────────────────────────────────────────────

    [Fact]
    public async Task ResidualPathFound_AppearsInResidualsFound()
    {
        const string path = @"C:\tmp\drDxtTemp.log";
        var paths = new FakeResidualPathInspector { Results = new string[] { path } };

        var report = await Make(residuals: paths).VerifyAsync(CancellationToken.None);

        report.ResidualsFound.Should().ContainSingle()
            .Which.Should().Contain(path);
    }

    // ── (5) service inspect returns Fail ──────────────────────────────────────

    [Fact]
    public async Task ServiceInspectionFails_NoServiceInstalledIsFalse_ErrorSurfaced()
    {
        var svc = new FakeServiceInspector
        {
            InspectResult = Result<bool>.Fail(ErrorCodes.DetectionFailed, "SCM access denied")
        };

        var report = await Make(services: svc).VerifyAsync(CancellationToken.None);

        report.NoServiceInstalled.Should().BeFalse(
            "cannot confirm clean when the SCM query failed");
        report.ResidualsFound.Should().ContainSingle()
            .Which.Should().Contain("service-inspect-error",
                "the error must be surfaced as a residual entry");
    }

    // ── (6) task inspect returns Fail ─────────────────────────────────────────

    [Fact]
    public async Task TaskInspectionFails_NoTaskInstalledIsFalse_ErrorSurfaced()
    {
        var sched = new FakeTaskSchedulerInspector
        {
            InspectResult = Result<IReadOnlyList<string>>.Fail(
                ErrorCodes.DetectionFailed, "Task Scheduler access denied")
        };

        var report = await Make(tasks: sched).VerifyAsync(CancellationToken.None);

        report.NoScheduledTaskInstalled.Should().BeFalse();
        report.ResidualsFound.Should().ContainSingle()
            .Which.Should().Contain("task-inspect-error");
    }

    // ── (7) all inspectors throw — VerifyAsync must NOT throw ─────────────────

    [Fact]
    public async Task AllInspectorsThrow_VerifyAsyncDoesNotThrow_AllErrorsSurfaced()
    {
        var svc   = new FakeServiceInspector      { Throws = true };
        var sched = new FakeTaskSchedulerInspector { Throws = true };
        var paths = new FakeResidualPathInspector  { Throws = true };
        var verifier = Make(svc, sched, paths);

        CleanupVerifyReport? report = null;
        Func<Task> act = async () =>
        {
            report = await verifier.VerifyAsync(CancellationToken.None);
        };

        await act.Should().NotThrowAsync("VerifyAsync must never throw");

        report.Should().NotBeNull();
        report!.NoServiceInstalled.Should().BeFalse("threw → cannot confirm clean");
        report.NoScheduledTaskInstalled.Should().BeFalse("threw → cannot confirm clean");
        report.ResidualsFound.Count.Should().BeGreaterThanOrEqualTo(2,
            "service-inspect-threw and task-inspect-threw are both surfaced");
    }

    // ── factory ───────────────────────────────────────────────────────────────

    private static RealHarnessCleanupVerifier Make(
        IServiceInspector?       services  = null,
        ITaskSchedulerInspector? tasks     = null,
        IResidualPathInspector?  residuals = null)
        => new(
            services  ?? new FakeServiceInspector(),
            tasks     ?? new FakeTaskSchedulerInspector(),
            residuals ?? new FakeResidualPathInspector());
}

// ── Recording fakes ───────────────────────────────────────────────────────────

internal sealed class FakeServiceInspector : IServiceInspector
{
    /// <summary>Result returned by IsInstalled. Default = Ok(false) — not installed.</summary>
    public Result<bool> InspectResult { get; set; } = Result<bool>.Ok(false);

    /// <summary>When true, IsInstalled throws instead of returning InspectResult.</summary>
    public bool Throws { get; set; }

    public Result<bool> IsInstalled(string serviceName)
    {
        if (Throws) throw new InvalidOperationException("Simulated SCM failure");
        return InspectResult;
    }
}

internal sealed class FakeTaskSchedulerInspector : ITaskSchedulerInspector
{
    /// <summary>Result returned by FindTasksMatching. Default = Ok([]) — no tasks.</summary>
    public Result<IReadOnlyList<string>> InspectResult { get; set; } =
        Result<IReadOnlyList<string>>.Ok(Array.Empty<string>());

    /// <summary>When true, FindTasksMatching throws instead of returning InspectResult.</summary>
    public bool Throws { get; set; }

    public Result<IReadOnlyList<string>> FindTasksMatching(string nameSubstring)
    {
        if (Throws) throw new InvalidOperationException("Simulated Task Scheduler failure");
        return InspectResult;
    }
}

internal sealed class FakeResidualPathInspector : IResidualPathInspector
{
    /// <summary>Paths returned as "existing". Default = empty — nothing found.</summary>
    public IReadOnlyList<string> Results { get; set; } = Array.Empty<string>();

    /// <summary>When true, FindExisting throws instead of returning Results.</summary>
    public bool Throws { get; set; }

    public IReadOnlyList<string> FindExisting(IReadOnlyList<string> paths)
    {
        if (Throws) throw new InvalidOperationException("Simulated path check failure");
        return Results;
    }
}
