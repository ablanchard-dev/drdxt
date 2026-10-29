using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.ReconcilerHost;

// BootSessionMarker : le compteur « homme mort » de boucle de démarrage, mené depuis le cycle de vie de l'hôte (plan
// T27b). MarkBootStarted est un +1 sans condition par démarrage ; seule une vraie fenêtre de marche stable l'efface :
//   * MarkBootOnceAsync incrémente une fois par processus (une garde empêche le double compte) ; les démarrages qui
//     plantent avant la fenêtre s'accumulent en une boucle que BootLoopGuard détecte ;
//   * MarkStableAfterAsync attend la fenêtre (IDelay injecté) puis remet la série à zéro (MarkSessionStable) ; hôte mort
//     ou annulé avant, rien n'est marqué.
// Temps injecté (testée sans vrai minuteur) ; le store est le même IBootOutcomeStore que lit la garde.
public sealed class BootSessionMarker
{
    private readonly IBootOutcomeStore _bootStore;
    private readonly IDelay _delay;

    // Limite MarkBootStarted à une seule fois par processus. Un marquage raté ne déclenche pas
    // la garde (un échec passager du store peut donc être repris).
    private int _bootMarked;

    public BootSessionMarker(IBootOutcomeStore bootStore, IDelay delay)
    {
        _bootStore = bootStore;
        _delay = delay;
    }

    /// <summary>Marque ce démarrage comme commencé (série de démarrages non propres +1) exactement
    /// une fois par processus. Un second appel est un Ok sans effet. Un échec du store rend Fail sans
    /// consommer la garde (l'appel suivant peut réessayer).</summary>
    public async Task<Result> MarkBootOnceAsync(CancellationToken ct)
    {
        // Prendre le créneau unique. CompareExchange rend la valeur précédente : non nulle veut dire
        // déjà marqué → sans effet.
        if (Interlocked.CompareExchange(ref _bootMarked, 1, 0) != 0)
            return Result.Ok();

        var marked = await _bootStore.MarkBootStartedAsync(ct);
        if (!marked.IsOk)
        {
            // Libérer le créneau unique pour qu'un échec passager puisse être repris.
            Interlocked.Exchange(ref _bootMarked, 0);
            return marked;
        }

        return Result.Ok();
    }

    /// <summary>Attend une fenêtre de marche stable puis marque la session stable (remet à zéro la
    /// série de démarrages non propres). L'attente passe par l'IDelay injecté ; une annulation avant
    /// la fin de la fenêtre se propage et la session n'est pas marquée stable (homme mort).</summary>
    public async Task<Result> MarkStableAfterAsync(TimeSpan window, CancellationToken ct)
    {
        // Une annulation lève OperationCanceledException (« n'a pas tenu la fenêtre ») : la stabilité n'est pas marquée.
        await _delay.DelayAsync(window, ct).ConfigureAwait(false);
        return await _bootStore.MarkSessionStableAsync(ct).ConfigureAwait(false);
    }
}
