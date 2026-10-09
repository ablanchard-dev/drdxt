using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// The reconciler's own behavioural seams (plan §4/§6/§9). The public IReconciler
// / IDesiredStateStore contracts live in DrDxt.Core; these are the module-local
// collaborators those implementations depend on.
// -----------------------------------------------------------------------

/// <summary>Non-blocking user notification raised when a drift is classified
/// UserIntentional/Unknown and the conservative policy is "notify, don't overwrite"
/// (I5 / §16). Carries resx KEYS, never hard-coded UI text (i18n, §4). In the host
/// this maps to a toast / event-log entry; here it's an injected seam.</summary>
public interface IReconcilerNotifier
{
    Task NotifyUserIntentionalDriftAsync(AppliedTweak tweak, string currentValueJson, CancellationToken ct);
}

/// <summary>Maps a reconcilable tweakId + its desired serialized value to the
/// Existing typed, allowlisted PrivilegedOp and dispatches it through the same
/// IElevatedClient pipeline (plan §9 / T32). There is NO new privileged primitive:
/// re-apply reuses the exact leaves the Application layer already uses. An unknown
/// tweakId fails before any IPC (I3).</summary>
public interface IElevatedReapplyClient
{
    Task<Result> ReapplyAsync(string tweakId, string desiredValueJson, CancellationToken ct);

    /// <summary>Re-asserts a reconcilable tweak to its original (pre-DrDXT) value for a
    /// clean uninstall — the inverse of <see cref="ReapplyAsync"/>. It carries the same
    /// security properties (I3, the WinRing0 lesson): the value flows only into the
    /// bounded value field of the existing typed op leaf the tweakId maps to, the op's
    /// TARGET is descriptor-only (never steered by the value), and an unknown / unbindable
    /// tweakId fails before any IPC. The default routes through the same map → bounded-op
    /// → elevated-dispatch path as re-apply with the original value in place of desired —
    /// no new privileged primitive, no off-manifest target.</summary>
    Task<Result> RevertToOriginalAsync(string tweakId, string originalValueJson, CancellationToken ct)
        => ReapplyAsync(tweakId, originalValueJson, ct);
}

/// <summary>The clean-uninstall mechanic (plan T37). On uninstall the host must leave
/// NO orphan: it STOPS the reconcile loop, DISARMS any pending dead-man boot-stable
/// scheduling / armed auto-revert lease, OPTIONALLY reverts every manifest item to its
/// recorded original value (through the same bounded typed-op path — I3 preserved), and
/// REMOVES the host's own service/task registration. The interface is pure (only Result);
/// the concrete implementation lives in the host, where stop/remove are host concerns.
/// M5 wires the UI/SCM; here the mechanic is built + tested with fakes (no real SCM).</summary>
public interface IReconcilerLifecycle
{
    /// <summary>Stops + disarms the reconciler and removes its host registration. When
    /// <paramref name="revertToOriginal"/> is true, each manifest item is first reverted
    /// to its OriginalValueJson (read-back confirmed) via the existing reapply path. The
    /// returned Result aggregates any per-item revert failure, but the loop is always
    /// stopped, the lease always disarmed, and the host always removed — a partial revert
    /// failure never leaves an orphaned, still-reconciling install.</summary>
    Task<Result> StopAndDisarmAsync(bool revertToOriginal, CancellationToken ct);
}

/// <summary>The persisted boot-outcome counter behind a seam (plan T27b). The host
/// marks a boot as "started" early and "stable" after a clean session window; the
/// guard reads the streak to decide whether GPU-tuning re-apply must be suspended.
/// Abstracted so unit tests drive the streak with a fake.
///
/// Dead-man pattern: MarkBootStarted INCREMENTS the streak before the reconcile pass,
/// MarkSessionStable RESETS it to 0 once the host has been up a stable interval. A
/// boot that never reaches a stable session leaves the streak elevated; consecutive
/// such boots accumulate into a detectable boot-loop.</summary>
public interface IBootOutcomeStore
{
    /// <summary>The current consecutive unclean-boot streak.</summary>
    Task<Result<int>> GetUncleanStreakAsync(CancellationToken ct);

    /// <summary>Called at host startup before the reconcile pass: increments the
    /// unclean-boot streak by 1 and persists it. If this boot later reaches a stable
    /// session the streak is reset; if it does not, the elevated streak is what the
    /// next boot's guard sees.</summary>
    Task<Result> MarkBootStartedAsync(CancellationToken ct);

    /// <summary>Called once the host has been up a stable interval: resets the
    /// unclean-boot streak to 0, stamps the last-stable-session timestamp, and
    /// persists. This is the "dead-man reset" that clears the boot-loop counter.</summary>
    Task<Result> MarkSessionStableAsync(CancellationToken ct);
}
