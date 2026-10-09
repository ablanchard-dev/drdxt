using DrDxt.Core;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// SingleFlightReconcile (plan T29) — serializes reconcile passes.
//
// Multiple triggers can fire near-simultaneously (boot + logon + a feature-update
// event + the daily timer all landing together). Running overlapping passes would
// race the read-modify-write of the sidecar state and could double-apply. A
// SemaphoreSlim(1,1) guarantees ONE pass at a time: a second trigger arriving while a
// pass runs WAITS for the slot, then runs once — never an overlapping double-run.
//
// The actual pass is injected as a delegate (the host passes ReconcileTickService
// .RunTickAsync), so this coordinator is a tiny, fully-unit-testable wrapper with no
// dependency on the reconcile internals. Disposable: the semaphore is owned here.
// -----------------------------------------------------------------------
public sealed class SingleFlightReconcile : IDisposable
{
    private readonly Func<CancellationToken, Task<Result>> _runTick;
    private readonly SemaphoreSlim _slot = new(1, 1);

    public SingleFlightReconcile(Func<CancellationToken, Task<Result>> runTick)
        => _runTick = runTick;

    /// <summary>Runs one reconcile pass under the single-flight slot. Concurrent calls
    /// serialize: at most one pass executes at a time; the others wait then run.</summary>
    public async Task<Result> RunTickAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _runTick(ct).ConfigureAwait(false);
        }
        finally
        {
            _slot.Release();
        }
    }

    public void Dispose() => _slot.Dispose();
}
