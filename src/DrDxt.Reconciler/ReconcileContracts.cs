using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// Les coutures de comportement propres au réconciliateur. Les contrats publics IReconciler /
// IDesiredStateStore vivent dans DrDxt.Core ; voici les collaborateurs locaux au module dont
// dépendent leurs implémentations.
// -----------------------------------------------------------------------

/// <summary>Notification non bloquante levée quand une dérive est classée UserIntentional/Unknown
/// et que la politique prudente est « notifier, ne pas écraser ». Porte des clés resx, jamais de
/// texte d'interface en dur (i18n). Dans l'hôte, cela devient un toast / une entrée du journal
/// d'événements ; ici, c'est une couture injectée.</summary>
public interface IReconcilerNotifier
{
    Task NotifyUserIntentionalDriftAsync(AppliedTweak tweak, string currentValueJson, CancellationToken ct);
}

/// <summary>Traduit un tweakId réconciliable + sa valeur voulue sérialisée en la PrivilegedOp
/// typée et admise existante, et l'envoie par le même pipeline IElevatedClient. Aucune nouvelle
/// primitive privilégiée : la réapplication reprend exactement les feuilles qu'utilise déjà la
/// couche Application. Un tweakId inconnu échoue avant tout IPC (I3).</summary>
public interface IElevatedReapplyClient
{
    Task<Result> ReapplyAsync(string tweakId, string desiredValueJson, CancellationToken ct);
}

/// <summary>Le compteur persisté d'issue des démarrages, derrière une couture (plan T27b ; un faux en test). Motif
/// « homme mort » : MarkBootStarted incrémente la série avant la passe de réconciliation, MarkSessionStable la remet à 0
/// après un intervalle stable ; des démarrages sans session stable s'accumulent en une boucle que la garde détecte pour
/// suspendre les réglages GPU.</summary>
public interface IBootOutcomeStore
{
    /// <summary>La série actuelle de démarrages non propres consécutifs.</summary>
    Task<Result<int>> GetUncleanStreakAsync(CancellationToken ct);

    /// <summary>Appelé au démarrage de l'hôte avant la passe de réconciliation : incrémente de 1 la
    /// série de démarrages non propres et l'enregistre. Si ce démarrage atteint ensuite une session
    /// stable, la série est remise à zéro ; sinon, c'est la série élevée que voit la garde du
    /// démarrage suivant.</summary>
    Task<Result> MarkBootStartedAsync(CancellationToken ct);

    /// <summary>Appelé une fois l'hôte resté en marche un intervalle stable : remet à 0 la série de
    /// démarrages non propres, horodate la dernière session stable et enregistre. C'est la remise à
    /// zéro « homme mort » qui efface le compteur de boucle de démarrage.</summary>
    Task<Result> MarkSessionStableAsync(CancellationToken ct);
}
