using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// BootSessionMarker (planification de l'issue des démarrages) : mène le compteur « homme mort »
// de boucle de démarrage depuis le cycle de vie de l'hôte (plan T27b).
//
// L'invariant : MarkBootStarted est un +1 sans condition par démarrage ; seule une vraie fenêtre
// de marche stable l'efface. Donc :
//   * MarkBootOnceAsync incrémente la série de démarrages non propres exactement une fois par
//     processus (une garde empêche un double compte si l'hôte le rappelle). Un démarrage qui
//     plante avant la fenêtre stable reste compté : ces démarrages consécutifs s'accumulent en
//     une boucle de démarrage que BootLoopGuard détecte ensuite.
//   * MarkStableAfterAsync attend une fenêtre de marche stable (via l'IDelay injecté) et ne remet
//     la série à zéro (MarkSessionStable) qu'ensuite. Si l'hôte meurt ou est annulé avant la fin
//     de la fenêtre, la stabilité n'est jamais marquée : la garantie « homme mort » tient.
//
// Tout le temps est injecté (IDelay) : la classe se teste sans vrai minuteur ; le store des
// démarrages est le même IBootOutcomeStore que lit la garde.
// -----------------------------------------------------------------------
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
        // Une annulation lève ici une OperationCanceledException, le bon signal « n'a pas tenu la
        // fenêtre » : on ne marque exprès pas la stabilité.
        await _delay.DelayAsync(window, ct).ConfigureAwait(false);
        return await _bootStore.MarkSessionStableAsync(ct).ConfigureAwait(false);
    }
}
