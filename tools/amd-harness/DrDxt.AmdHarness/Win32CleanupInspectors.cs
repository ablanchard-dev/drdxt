using System.Diagnostics;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.AmdHarness;

// -----------------------------------------------------------------------
// Production Win32-backed implementations of the cleanup inspector seams.
// All three are deliberately thin wrappers so the seams can be replaced
// by fakes in unit tests without touching any OS state.
//
// No additional NuGet packages: sc.exe and schtasks.exe are always present
// on Windows; File/Directory are BCL. ServiceController is intentionally
// NOT used here (it requires the Windows Desktop SDK which a portable
// console tool does not reference).
// -----------------------------------------------------------------------

/// <summary>SCM inspector: shells out to <c>sc.exe query &lt;name&gt;</c> to check
/// whether a named Windows service is installed. Exit code 0 = service exists;
/// exit code 1060 (service not found) or any other non-zero = not installed.</summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32ServiceInspector : IServiceInspector
{
    public Result<bool> IsInstalled(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query \"{serviceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return Result<bool>.Fail(ErrorCodes.DetectionFailed,
                    "sc.exe could not be started.");

            // Drain stdout so the process is not blocked waiting for us to read.
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            // Exit code 0 = service exists (in any state: running, stopped, etc.).
            // Exit code 1060 = "The specified service does not exist as an installed service."
            // Any other non-zero is also treated as "not found" — we never hide uncertainty.
            return Result<bool>.Ok(proc.ExitCode == 0);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ErrorCodes.DetectionFailed,
                $"Service check failed: {ex.Message}");
        }
    }
}

/// <summary>Task Scheduler inspector: runs <c>schtasks /query /fo CSV /nh</c>
/// and returns all task names containing the given substring.</summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32TaskSchedulerInspector : ITaskSchedulerInspector
{
    public Result<IReadOnlyList<string>> FindTasksMatching(string nameSubstring)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", "/query /fo CSV /nh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return Result<IReadOnlyList<string>>.Fail(ErrorCodes.DetectionFailed,
                    "schtasks.exe could not be started.");

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            var matched = new List<string>();
            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var taskName = ExtractFirstCsvField(raw.Trim());
                if (!string.IsNullOrEmpty(taskName) &&
                    taskName.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(taskName);
                }
            }

            return Result<IReadOnlyList<string>>.Ok(matched);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<string>>.Fail(ErrorCodes.DetectionFailed,
                $"Task Scheduler query failed: {ex.Message}");
        }
    }

    // CSV first field: either a quoted string ("value") or an unquoted token.
    private static string ExtractFirstCsvField(string line)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;
        if (line[0] == '"')
        {
            int end = line.IndexOf('"', 1);
            return end > 0 ? line[1..end] : line;
        }
        int comma = line.IndexOf(',');
        return comma >= 0 ? line[..comma] : line;
    }
}

/// <summary>File/directory inspector: returns whichever of the given paths
/// exist on disk (checked via File.Exists / Directory.Exists).</summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32ResidualPathInspector : IResidualPathInspector
{
    public IReadOnlyList<string> FindExisting(IReadOnlyList<string> paths)
    {
        var existing = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path) || Directory.Exists(path))
                existing.Add(path);
        }
        return existing;
    }
}
