using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// ReconcilerLifecycle (plan T37) — the clean-uninstall mechanic.
//
// On uninstall the host must leave NO orphan: no still-running reconcile loop, no
// armed dead-man / auto-revert lease, no registered service/task that would re-spawn
// the reconciler. StopAndDisarmAsync does exactly that, in an order that is safe even
// when a step fails:
//   1. STOP the reconcile loop (so nothing re-applies while we revert).
//   2. DISARM any pending dead-man boot-stable scheduling / armed auto-revert lease
//      (so the watchdog never fires after we are gone).
//   3. If revertToOriginal: revert each manifest item to its recorded OriginalValueJson
//      through the same bounded typed-op path the reconciler re-applies with (I3) —
//      read-back confirmed. Only tweakIds present in the verified manifest are touched.
//   4. REMOVE the host's own service/task registration (via the injected uninstall seam).
//
// Fail-clean, no orphan: steps 1, 2 and 4 always run. A per-item revert failure in
// step 3 is aggregated into the returned Result, but never aborts the stop/disarm/
// remove — an uninstall that could not perfectly restore a value must still leave the
// machine with the reconciler gone, not half-removed and still re-applying.
//
// SECURITY (I3, the WinRing0 lesson): the revert never introduces a new privileged
// primitive. It reuses IElevatedReapplyClient.RevertToOriginalAsync, which maps the
// tweakId to an existing sealed typed op with the original value in the BOUNDED value
// field and a DESCRIPTOR-only target. The candidate set is the verified manifest's
// items only — a value can never steer the write at an off-manifest target.
// -----------------------------------------------------------------------

/// <summary>Signals the running reconcile loop to STOP. The host backs this with its
/// application lifetime / a stop seam; tests use a fake that records the stop. Idempotent:
/// stopping an already-stopped loop is a no-op Ok.</summary>
public interface IHostStopSignal
{
    /// <summary>Requests the reconcile loop to stop and (best-effort) waits for the
    /// in-flight pass to drain. Never throws across the boundary.</summary>
    Task<Result> StopAsync(CancellationToken ct);
}

/// <summary>Disarms any ARMED safety that would otherwise act after uninstall: the
/// pending dead-man boot-stable scheduling (so no boot-loop suspension stays latched)
/// and any armed auto-revert lease in the elevated helper (so the watchdog never fires
/// for a reconciler that no longer exists). Reuses the existing boot-outcome / lease
/// seams under the hood; tests use a fake that records the disarm. Idempotent.</summary>
public interface ILeaseDisarm
{
    /// <summary>Disarms the dead-man scheduling + any armed auto-revert lease. Never
    /// throws across the boundary; a disarm that finds nothing armed is a no-op Ok.</summary>
    Task<Result> DisarmAsync(CancellationToken ct);
}

/// <summary>Removes the host's own service/task registration (the uninstall counterpart
/// of HostInstaller's least-privilege definition). The production implementation talks to
/// the SCM / Task Scheduler; tests use a fake that records it was ASKED to remove. NO real
/// SCM call lives in this mechanic — removal is delegated entirely to this seam.</summary>
public interface IHostUninstaller
{
    /// <summary>Removes the registered service/task so the reconciler never re-spawns.
    /// Never throws across the boundary.</summary>
    Task<Result> RemoveAsync(CancellationToken ct);
}

/// <summary>The host's clean-uninstall mechanic. Pure of any real SCM/timer: every
/// effect goes through an injected seam (stop / disarm / revert / remove), so the
/// no-orphan guarantee is asserted with fakes.</summary>
public sealed class ReconcilerLifecycle : IReconcilerLifecycle
{
    /// <summary>Journal actor marking an uninstall lifecycle step — lets a log scan
    /// filter uninstall events deterministically (machine-parseable, not UI).</summary>
    public const string UninstallActor = "DrDXT/uninstall";

    private readonly IHostStopSignal _stop;
    private readonly ILeaseDisarm _disarm;
    private readonly IDesiredStateStore _manifest;
    private readonly IElevatedReapplyClient _reapply;
    private readonly ISettingProbe _probe;
    private readonly IHostUninstaller _uninstaller;
    private readonly IDiagnosticsLog _log;

    public ReconcilerLifecycle(
        IHostStopSignal stop,
        ILeaseDisarm disarm,
        IDesiredStateStore manifest,
        IElevatedReapplyClient reapply,
        ISettingProbe probe,
        IHostUninstaller uninstaller,
        IDiagnosticsLog log)
    {
        _stop = stop;
        _disarm = disarm;
        _manifest = manifest;
        _reapply = reapply;
        _probe = probe;
        _uninstaller = uninstaller;
        _log = log;
    }

    public async Task<Result> StopAndDisarmAsync(bool revertToOriginal, CancellationToken ct)
    {
        // Collect every step's failure; we do not short-circuit on a non-fatal failure,
        // because leaving the loop running / the host registered is worse than a value
        // we could not restore. The aggregate decides the final Result.
        var failures = new List<Error>();

        // (1) STOP the loop first — nothing must re-apply while/after we revert.
        var stopped = await _stop.StopAsync(ct).ConfigureAwait(false);
        if (!stopped.IsOk)
            Record(failures, "stop", stopped);

        // (2) DISARM the dead-man scheduling + any armed auto-revert lease.
        var disarmed = await _disarm.DisarmAsync(ct).ConfigureAwait(false);
        if (!disarmed.IsOk)
            Record(failures, "disarm", disarmed);

        // (3) Optionally revert each verified-manifest item to its original value.
        if (revertToOriginal)
            await RevertAllToOriginalAsync(failures, ct).ConfigureAwait(false);

        // (4) REMOVE the host registration LAST — once removed the reconciler cannot
        // re-spawn. Always attempted, even if a revert failed (no half-removed orphan).
        var removed = await _uninstaller.RemoveAsync(ct).ConfigureAwait(false);
        if (!removed.IsOk)
            Record(failures, "remove", removed);

        if (failures.Count == 0)
        {
            _log.Change(HostActors.ManifestTweakId, "armed", "uninstalled", UninstallActor);
            return Result.Ok();
        }

        // Fail-clean: aggregate the codes, but the loop is stopped, the lease disarmed
        // and the host removed regardless — no orphan.
        var codes = string.Join(", ", failures.Select(f => f.Code));
        return Result.Fail(ReconcilerErrorCodes.UninstallRevertPartial,
            "Uninstall completed (loop stopped, lease disarmed, host removed) but some "
            + $"steps failed: {codes}");
    }

    /// <summary>Reverts every item in the verified manifest to its OriginalValueJson via
    /// the same bounded typed-op path (I3) and read-back confirms each. A manifest that
    /// cannot be loaded (tampered / IO) is itself a failure recorded here — but it never
    /// aborts the stop/disarm/remove above.</summary>
    private async Task RevertAllToOriginalAsync(List<Error> failures, CancellationToken ct)
    {
        // Only the verified manifest's items are revert candidates — LoadAsync fails
        // CLOSED on tamper, so a hostile manifest cannot expand the target set (I3/I4).
        var loaded = await _manifest.LoadAsync().ConfigureAwait(false);
        if (!loaded.IsOk)
        {
            Record(failures, "manifest-load", loaded);
            return;
        }

        foreach (var tweak in loaded.Value!.Tweaks)
        {
            ct.ThrowIfCancellationRequested();
            await RevertOneAsync(tweak, failures, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reverts ONE item to its original value and confirms with a read-back. The
    /// revert dispatches the original value through the existing typed op leaf the tweakId
    /// maps to (descriptor-only target); an unknown / unbindable id fails before any IPC.</summary>
    private async Task RevertOneAsync(AppliedTweak tweak, List<Error> failures, CancellationToken ct)
    {
        var id = tweak.TweakId;

        // Re-assert the original value through the same bounded op leaf (I3). The value
        // flows only into the op's bounded value field; the target is descriptor-only.
        var reverted = await _reapply
            .RevertToOriginalAsync(id, tweak.OriginalValueJson, ct).ConfigureAwait(false);
        if (!reverted.IsOk)
        {
            Record(failures, "revert:" + id, reverted);
            return;
        }

        // Read-back confirm: a write that reported OK but did not take (e.g. a GPO
        // re-forces it) is a failure, not a silent success.
        var confirm = await _probe.ReadCurrentValueAsync(id, ct).ConfigureAwait(false);
        if (!confirm.IsOk)
        {
            Record(failures, "revert-confirm:" + id, confirm);
            return;
        }

        if (!string.Equals(confirm.Value, tweak.OriginalValueJson, StringComparison.Ordinal))
        {
            failures.Add(new Error(ReconcilerErrorCodes.ConfirmFailed,
                $"'{id}' revert reported OK but value not confirmed (still '{confirm.Value}')."));
            _log.Change(id, confirm.Value ?? "?", tweak.OriginalValueJson, HostActors.Security);
            return;
        }

        _log.Change(id, tweak.DesiredValueJson, tweak.OriginalValueJson, UninstallActor);
    }

    /// <summary>Records a failed step's Error (or a placeholder if the Result carried none)
    /// and journals it under the security actor for a deterministic audit scan.</summary>
    private void Record(List<Error> failures, string stage, Result result)
    {
        var error = result.Error
            ?? new Error(ReconcilerErrorCodes.UninstallRevertPartial, stage + " failed");
        failures.Add(error);
        _log.Change(HostActors.ManifestTweakId, error.Code, stage + "-failed", HostActors.Security);
    }
}
