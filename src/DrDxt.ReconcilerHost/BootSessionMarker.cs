using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// BootSessionMarker (boot-outcome scheduling) — drives the dead-man boot-loop
// counter from the host lifecycle (plan T27b).
//
// THE INVARIANT: MarkBootStarted is an UNCONDITIONAL +1 per boot; only a genuine
// stable up-window clears it. So:
//   * MarkBootOnceAsync increments the unclean streak exactly ONCE per process (a
//     guard prevents a double-count if the host calls it again). A boot that crashes
//     before the stable window stays counted — consecutive such boots accumulate into
//     a boot-loop the BootLoopGuard then detects.
//   * MarkStableAfterAsync waits a stable up-window (via the injected IDelay) and only
//     Then resets the streak (MarkSessionStable). If the host dies / is cancelled
//     before the window elapses, stable is never marked — preserving the dead-man
//     guarantee.
//
// All time is injected (IDelay) so this is unit-tested with no real timer; the boot
// store is the same IBootOutcomeStore the guard reads.
// -----------------------------------------------------------------------
public sealed class BootSessionMarker
{
    private readonly IBootOutcomeStore _bootStore;
    private readonly IDelay _delay;

    // Guards MarkBootStarted to exactly-once per process. A FAILED mark does not trip
    // the guard (so a transient store failure can be retried).
    private int _bootMarked;

    public BootSessionMarker(IBootOutcomeStore bootStore, IDelay delay)
    {
        _bootStore = bootStore;
        _delay = delay;
    }

    /// <summary>Marks this boot as started (unclean streak +1) exactly once per process.
    /// A second call is a no-op Ok. A store failure returns Fail without consuming the
    /// once-guard (the next call may retry).</summary>
    public async Task<Result> MarkBootOnceAsync(CancellationToken ct)
    {
        // Claim the once-slot. CompareExchange returns the PRIOR value: non-zero means
        // already marked → no-op.
        if (Interlocked.CompareExchange(ref _bootMarked, 1, 0) != 0)
            return Result.Ok();

        var marked = await _bootStore.MarkBootStartedAsync(ct);
        if (!marked.IsOk)
        {
            // Release the once-slot so a transient failure can be retried.
            Interlocked.Exchange(ref _bootMarked, 0);
            return marked;
        }

        return Result.Ok();
    }

    /// <summary>Waits a stable up-window then marks the session stable (resets the
    /// unclean streak). The wait uses the injected IDelay; cancellation before the
    /// window elapses propagates and the session is not marked stable (dead-man).</summary>
    public async Task<Result> MarkStableAfterAsync(TimeSpan window, CancellationToken ct)
    {
        // Cancellation here throws OperationCanceledException, which is the correct
        // "did not survive the window" signal — we deliberately do not mark stable.
        await _delay.DelayAsync(window, ct).ConfigureAwait(false);
        return await _bootStore.MarkSessionStableAsync(ct).ConfigureAwait(false);
    }
}
