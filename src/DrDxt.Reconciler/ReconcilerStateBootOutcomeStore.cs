using DrDxt.Core;

namespace DrDxt.Reconciler;

// ReconcilerStateBootOutcomeStore : l'écriture du compteur anti-boucle de démarrage (plan T27b) dans le sidecar
// d'exécution. Pur : seulement IReconcilerStateStore + IClock (Core). Motif « homme mort » :
//   • MarkBootStarted (démarrage de l'hôte, avant la passe de réconciliation) incrémente la
//     série. Un démarrage qui n'atteint jamais une session stable la laisse élevée.
//   • MarkSessionStable (après un intervalle de marche stable) remet la série à 0 et horodate
//     LastStableSessionAt : la remise à zéro « homme mort » qui efface le compteur.
//   • GetUncleanStreak ne fait que la lire (ce que consomme BootLoopGuard).
// Chaque modification est Load → record-with → Save d'un seul tenant (MettreAJourAsync) : un échec rend son Fail, sans
// modification partielle ni exception. Le sidecar n'a aucune autorité signée : l'écrire n'élargit aucun privilège.
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

    /// <summary>Load → modifier → Save d'un seul tenant (MettreAJourAsync), en échec propre. Un Load raté court-circuite
    /// avant toute modification ; un Save raté se propage sans avoir modifié l'état à moitié.</summary>
    private async Task<Result> MutateAsync(
        Func<ReconcilerState, ReconcilerState> mutate, CancellationToken ct)
    {
        var r = await _stateStore.MettreAJourAsync(mutate, ct);
        return r.IsOk ? Result.Ok() : Result.Fail(r.Error!.Value.Code, r.Error.Value.Message);
    }
}
