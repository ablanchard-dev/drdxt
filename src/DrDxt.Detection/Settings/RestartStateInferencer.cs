using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Answers the one question every A/B run and every "did my tweak stick?" claim depends on:
/// <b>has a change that requires a restart actually taken effect?</b>
///
/// Two traps this exists to avoid, both observed in the wild:
///
/// 1. <b>PendingFileRenameOperations is not a reboot flag.</b> Off-the-shelf "reboot pending"
///    checks test its mere presence and light up on a perfectly healthy PC — the value routinely
///    holds benign entries queued by ordinary servicing. Here it never alone yields
///    <see cref="RestartVerdict.RestartRequired"/>; the authoritative signals are the servicing
///    keys whose very existence is the flag by Microsoft's own design
///    (Component Based Servicing\RebootPending, WindowsUpdate\Auto Update\RebootRequired).
///    Source: https://learn.microsoft.com/en-us/windows/deployment/update/waas-restart
///
/// 2. <b>Fast Startup makes "I shut it down" a lie.</b> With HiberbootEnabled, "Shut down" then
///    power on hibernates and restores the kernel session — it does not reload drivers or apply
///    boot-time settings. Only "Restart" does a real cold kernel init. So a short uptime does not
///    prove a restart happened, and a change can silently look "reverted". When Fast Startup is on
///    we refuse to conclude and say which button the user must press, rather than claim Applied.
///    Source: https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/updates-not-install-with-fast-startup
///    (« When you shut down your computer, Fast Startup will put your computer into a hibernation state
///    instead of a full shutdown » ; « This behavior doesn't occur when you restart your computer ».
///    L'ancienne page windows-fast-startup répond 404, relevé le 25/09/2026.)
///
/// Pure and total: no I/O, no clock. All OS reads are the caller's; nulls mean "not read", never
/// "absent" — an unreadable input degrades to <see cref="RestartVerdict.Indeterminate"/>, it never
/// invents a clean bill of health (§16 honesty, same contract as <see cref="PagefileInferencer"/>).
/// </summary>
public static class RestartStateInferencer
{
    /// <param name="servicingRebootPending">
    /// True if either servicing key exists, false if both were read and absent, null if unreadable.
    /// </param>
    /// <param name="pendingFileRenames">
    /// Raw PendingFileRenameOperations entries, empty if the value is absent, null if unreadable.
    /// Weak signal by construction — see trap 1.
    /// </param>
    /// <param name="fastStartup">HiberbootEnabled: On, Off, or Unknown.</param>
    /// <param name="uptime">Time since the last kernel init (GetTickCount64), null if unread.</param>
    /// <param name="sinceChange">
    /// Age of the change being verified, null when this is a cold diagnostic with no change to
    /// verify — then the verdict describes the machine, not a change.
    /// </param>
    public static RestartState Infer(
        bool? servicingRebootPending,
        IReadOnlyList<string>? pendingFileRenames,
        TriState fastStartup,
        TimeSpan? uptime,
        TimeSpan? sinceChange)
    {
        // A servicing key is authoritative on its own: Windows itself is waiting on a restart,
        // so nothing measured now describes the configuration that will run after it.
        if (servicingRebootPending == true)
            return new RestartState(RestartVerdict.RestartRequired,
                "Windows servicing has a restart pending: measurements taken now do not describe " +
                "the configuration that will be running after it.");

        var renamesQueued = pendingFileRenames is { Count: > 0 }
                            && pendingFileRenames.Any(e => !string.IsNullOrWhiteSpace(e));

        if (sinceChange is null)
            return ColdDiagnostic(servicingRebootPending, pendingFileRenames, renamesQueued);

        if (uptime is null)
            return new RestartState(RestartVerdict.Indeterminate,
                "Uptime could not be read, so we cannot tell whether the machine has restarted " +
                "since the change.");

        // Uptime at least as old as the change ⇒ this kernel session predates it ⇒ no restart since.
        if (uptime.Value >= sinceChange.Value)
            return new RestartState(RestartVerdict.RestartRequired,
                "The machine has not restarted since the change was written, so anything requiring " +
                "a restart is not in effect yet.");

        // A kernel init happened after the change. Whether it was a real one depends on Fast Startup.
        if (fastStartup == TriState.On)
            return new RestartState(RestartVerdict.Indeterminate,
                "The machine started after the change, but Fast Startup is enabled: a shutdown then " +
                "power-on restores the previous kernel session and does not reload drivers. Use " +
                "Restart, not Shut down, then check again.");

        if (fastStartup == TriState.Unknown)
            return new RestartState(RestartVerdict.Indeterminate,
                "The machine started after the change, but Fast Startup could not be read, so we " +
                "cannot tell whether that start was a real kernel init.");

        return new RestartState(RestartVerdict.NoRestartPending,
            renamesQueued
                ? "A real restart happened after the change: it is in effect. File renames are " +
                  "queued for the next restart, which is routine and not a defect."
                : "A real restart happened after the change: it is in effect.");
    }

    private static RestartState ColdDiagnostic(
        bool? servicingRebootPending, IReadOnlyList<string>? pendingFileRenames, bool renamesQueued)
    {
        if (servicingRebootPending is null && pendingFileRenames is null)
            return new RestartState(RestartVerdict.Indeterminate,
                "No restart indicator could be read on this machine.");

        // Trap 1: renames alone are the signal every off-the-shelf check gets wrong. We report it
        // as unproven, never as "restart required".
        if (renamesQueued)
            return new RestartState(RestartVerdict.Indeterminate,
                "File renames are queued for the next restart. On its own this is a weak signal (" +
                "ordinary servicing queues them on healthy machines), so it does not establish that " +
                "a restart is required.");

        return new RestartState(RestartVerdict.NoRestartPending,
            "No restart is pending on this machine.");
    }
}
