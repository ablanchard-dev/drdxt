using DrDxt.Core;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// SingleFlightReconcile : met les passes de réconciliation en file.
//
// Plusieurs déclencheurs peuvent partir presque en même temps (démarrage + ouverture de session +
// un événement de mise à jour de fonctionnalité + le minuteur quotidien, tous ensemble). Des
// passes qui se chevauchent entreraient en course sur la lecture-modification-écriture de l'état
// du sidecar et pourraient appliquer deux fois. Un SemaphoreSlim(1,1) garantit une seule passe à
// la fois : un second déclencheur arrivé pendant une passe attend le créneau, puis tourne une
// fois ; jamais deux passes qui se chevauchent.
//
// La passe elle-même est injectée comme délégué (l'hôte passe ReconcileTickService
// .RunTickAsync) : ce coordinateur est une petite enveloppe, entièrement testable, sans
// dépendance aux rouages de la réconciliation. Disposable : le sémaphore est possédé ici.
// -----------------------------------------------------------------------
public sealed class SingleFlightReconcile : IDisposable
{
    private readonly Func<CancellationToken, Task<Result>> _runTick;
    private readonly SemaphoreSlim _slot = new(1, 1);

    public SingleFlightReconcile(Func<CancellationToken, Task<Result>> runTick)
        => _runTick = runTick;

    /// <summary>Lance une passe de réconciliation dans le créneau unique. Les appels concurrents se
    /// mettent en file : une passe au plus tourne à la fois ; les autres attendent puis tournent.</summary>
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
