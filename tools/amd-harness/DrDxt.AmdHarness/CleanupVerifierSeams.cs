using DrDxt.Core;

namespace DrDxt.AmdHarness;

// -----------------------------------------------------------------------
// Seam interfaces for RealHarnessCleanupVerifier.
//
// Kept internal so the verifier can be unit-tested via fakes without
// exposing OS-coupling details in the public harness API.
// DrDxt.AmdHarness.Tests sees these via InternalsVisibleTo.
// -----------------------------------------------------------------------

/// <summary>Checks whether a named Windows service is installed via the SCM.</summary>
internal interface IServiceInspector
{
    /// <summary>Returns Ok(true) when the service exists, Ok(false) when absent,
    /// Fail when the SCM itself cannot be queried.</summary>
    Result<bool> IsInstalled(string serviceName);
}

/// <summary>Finds Task Scheduler tasks whose name contains a given substring.</summary>
internal interface ITaskSchedulerInspector
{
    /// <summary>Returns Ok with the matching task names (empty list = none found),
    /// Fail when the scheduler cannot be queried.</summary>
    Result<IReadOnlyList<string>> FindTasksMatching(string nameSubstring);
}

/// <summary>Checks which of a set of file/directory paths exist on disk.</summary>
internal interface IResidualPathInspector
{
    /// <summary>Returns the subset of <paramref name="paths"/> that exist on disk.
    /// Never returns null; may throw on unexpected OS failures (caller must guard).</summary>
    IReadOnlyList<string> FindExisting(IReadOnlyList<string> paths);
}
