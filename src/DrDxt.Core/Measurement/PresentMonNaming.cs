namespace DrDxt.Core;

/// <summary>
/// Le tube par lequel le client DrDXT parle a son service de mesure, et pourquoi il n'en existe qu'un sur la machine.
/// Mesure le 07/09/2026, par le SCM, sur la machine de reference : le binaire vendore accepte `--control-pipe`
/// (« Name of the named pipe to use for the client-service control channel ») sans erreur, mais :
///  1. Service DrDXT 2.4.1 demarre par le SCM avec
///     `--control-pipe \\.\pipe\drdxt-presentmon-control`, service Intel arrete :
///     il publie `sharedpresentmonsvcnamedpipe-in/-out`, **le nom par defaut**. L'option est
///     parsee et **sans effet sur le nom publie** : elle sert au client, pas au serveur.
///  2. Meme essai avec le binaire **Intel 2.5.1** (service jetable, supprime apres l'essai) : meme
///     resultat, nom par defaut. Monter la version vendorée ne rendrait donc pas l'option
///     operante. (2.5.1 refuse par ailleurs `--intro-nsm` : sa ligne de commande a change.)
///  3. Client 2.4.1 → service Intel 2.5.1, sur le tube par defaut : `PM_STATUS_PIPE_ERROR` (12).
///     Client 2.4.1 → service DrDXT 2.4.1, meme tube : **succes**.
///  4. Client **2.5.1** contre le service Intel **2.5.1 actif** : `pmOpenSession` et `pmStartTrackingProcess` sur un
///     vrai processus = succes ; notre client 2.4.1 rend PIPE_ERROR (instrument : `tools/pmonpair`).
/// Lance en console (`--debug`), le service ne cree jamais de tube de controle : **seule la mesure par le SCM compte**.
/// Un seul nom de tube publiable, un seul service pour le posseder ; une paire de versions differentes ne cohabite pas
/// avec le service Intel, une paire assortie si : le probleme est **la version du client vendoré**. Headers compares,
/// les **89 valeurs de `PM_METRIC` sont identiques et dans le meme ordre** (2.5.1 en ajoute trois, dont
/// `PM_METRIC_SESSION_START_QPC`) et `PM_QUERY_ELEMENT` est **inchangee** : seul le binaire change. Voir
/// `docs/PRESENTMON-COEXISTENCE.md` et le gate dans `GATES.md`.
/// </summary>
public static class PresentMonNaming
{
    /// <summary>Le tube de controle : le nom par defaut de PresentMon, que le serveur publie quoi qu'on lui passe
    /// (mesure 1). Nomme ici parce que le client l'ouvre explicitement (`pmOpenSessionWithPipe`), et qu'**une seule
    /// ligne** decide a quel service DrDXT parle.</summary>
    public const string ControlPipe = @"\\.\pipe\sharedpresentmonsvcnamedpipe";

    /// <summary>Les arguments du binaire du service : **aucun**. `--control-pipe` n'a pas d'effet sur le nom publie, et
    /// `--intro-nsm` / `--etw-session-name` n'ont **jamais ete mesures** comme en ayant un. La propriete reste (vide) :
    /// l'installeur et le helper elevé partagent cette source, et un test relit les deux ecritures.</summary>
    public static string ServiceArguments => string.Empty;
}
