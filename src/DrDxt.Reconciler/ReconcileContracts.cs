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

    /// <summary>Remet un levier réconciliable à sa valeur d'origine (d'avant DrDXT) pour une
    /// désinstallation propre : l'inverse de <see cref="ReapplyAsync"/>. Mêmes propriétés de sécurité
    /// (I3, la leçon WinRing0) : la valeur ne va que dans le champ de valeur borné de la feuille d'op
    /// typée existante associée au tweakId, la cible de l'op vient du seul descripteur (jamais guidée
    /// par la valeur), et un tweakId inconnu ou non reliable échoue avant tout IPC. Par défaut, passe
    /// par le même chemin traduction → op bornée → envoi élevé que la réapplication, avec la valeur
    /// d'origine à la place de la voulue : aucune nouvelle primitive privilégiée, aucune cible hors manifeste.</summary>
    Task<Result> RevertToOriginalAsync(string tweakId, string originalValueJson, CancellationToken ct)
        => ReapplyAsync(tweakId, originalValueJson, ct);
}

/// <summary>La mécanique de désinstallation propre. À la désinstallation, l'hôte ne doit laisser
/// aucun orphelin : il arrête la boucle de réconciliation, désarme toute planification « homme
/// mort » de démarrage stable en attente / tout bail de retour automatique armé, remet en option
/// chaque élément du manifeste à sa valeur d'origine enregistrée (par le même chemin d'op typée
/// bornée : I3 préservé), et retire son propre enregistrement de service / tâche. L'interface est
/// pure (Result seulement) ; l'implémentation concrète vit dans l'hôte, à qui reviennent arrêt et
/// retrait. L'hôte branche l'interface et le SCM ; ici la mécanique est construite et testée avec
/// des faux (sans vrai SCM).</summary>
public interface IReconcilerLifecycle
{
    /// <summary>Arrête et désarme le réconciliateur et retire l'enregistrement de son hôte. Si
    /// <paramref name="revertToOriginal"/> vaut true, chaque élément du manifeste est d'abord remis à
    /// son OriginalValueJson (confirmé par relecture) par le chemin de réapplication existant. Le
    /// Result rendu regroupe chaque échec de retour par élément, mais la boucle est toujours arrêtée,
    /// le bail toujours désarmé et l'hôte toujours retiré : un échec de retour partiel ne laisse
    /// jamais une installation orpheline qui réconcilie encore.</summary>
    Task<Result> StopAndDisarmAsync(bool revertToOriginal, CancellationToken ct);
}

/// <summary>Le compteur persisté d'issue des démarrages, derrière une couture (plan T27b). L'hôte
/// marque tôt un démarrage « commencé » et « stable » après une fenêtre de session propre ; la
/// garde lit la série pour décider si la réapplication des réglages GPU doit être suspendue.
/// Abstrait pour que les tests unitaires mènent la série avec un faux.
///
/// Motif « homme mort » : MarkBootStarted incrémente la série avant la passe de réconciliation,
/// MarkSessionStable la remet à 0 une fois l'hôte resté en marche un intervalle stable. Un
/// démarrage qui n'atteint jamais une session stable laisse la série élevée ; ces démarrages
/// consécutifs s'accumulent en une boucle de démarrage détectable.</summary>
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
