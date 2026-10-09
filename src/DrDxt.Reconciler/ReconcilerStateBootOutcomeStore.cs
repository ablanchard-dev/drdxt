using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// ReconcilerStateBootOutcomeStore — the write path of the anti boot-loop counter
// (plan T27b), persisting the streak in the runtime sidecar (§5). Pure: it depends
// only on the IReconcilerStateStore + IClock seams (Core), no OS calls of its own.
//
// Dead-man pattern:
//   • MarkBootStarted (host startup, before the reconcile pass) INCREMENTS the
//     streak. A boot that never reaches a stable session leaves it elevated.
//   • MarkSessionStable (after a stable up-interval) RESETS the streak to 0 and
//     stamps LastStableSessionAt — the dead-man reset that clears the counter.
//   • GetUncleanStreak just reads it (what BootLoopGuard consumes).
//
// Each mutation is Load → record-with → Save, fail-CLEAN: if Load or Save fails the
// op returns that Fail unchanged, with no partial mutation and no exception across
// the seam. The sidecar is unsigned authority-wise — it can only make the reconciler
// do LESS — so persisting it here never widens privilege.
// -----------------------------------------------------------------------
public sealed class ReconcilerStateBootOutcomeStore : IBootOutcomeStore
{
    private readonly IReconcilerStateStore _stateStore;
    private readonly IClock _clock;

    public ReconcilerStateBootOutcomeStore(IReconcilerStateStore stateStore, IClock clock)
    {
        _stateStore = stateStore;
        _clock = clock;
    }

    public async Task<Result<int>> GetUncleanStreakAsync(CancellationToken ct)
    {
        var loaded = await _stateStore.LoadAsync(ct);
        if (!loaded.IsOk)
            return Result<int>.Fail(loaded.Error!.Value.Code, loaded.Error.Value.Message);

        return Result<int>.Ok(loaded.Value!.UncleanBootStreak);
    }

    public Task<Result> MarkBootStartedAsync(CancellationToken ct)
        => MutateAsync(state => state with { UncleanBootStreak = state.UncleanBootStreak + 1 }, ct);

    public Task<Result> MarkSessionStableAsync(CancellationToken ct)
        => MutateAsync(
            state => state with { UncleanBootStreak = 0, LastStableSessionAt = _clock.UtcNow },
            ct);

    /// <summary>Load → mutate → Save, fail-clean. A failed Load short-circuits before
    /// any mutation; a failed Save propagates without having partially mutated state.</summary>
    private async Task<Result> MutateAsync(
        Func<ReconcilerState, ReconcilerState> mutate, CancellationToken ct)
    {
        var loaded = await _stateStore.LoadAsync(ct);
        if (!loaded.IsOk)
            return Result.Fail(loaded.Error!.Value.Code, loaded.Error.Value.Message);

        var next = mutate(loaded.Value!);
        return await _stateStore.SaveAsync(next, ct);
    }
}
