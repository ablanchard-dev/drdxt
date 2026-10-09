namespace DrDxt.Core;

/// <summary>
/// Le tube par lequel le client DrDXT parle a son service de mesure — et pourquoi il n'en existe
/// Qu'un seul sur la machine.
///
/// ══ ce qui a ete mesure ici le 07/09/2026, par le SCM, sur la machine de reference ══
/// L'hypothese de depart etait qu'un tube de controle prive reglerait la cohabitation avec le
/// service Intel. Le binaire vendore documente bien l'option dans ses propres chaines d'aide :
/// `--control-pipe` = « Name of the named pipe to use for the client-service control channel ».
/// Elle est acceptee sans erreur. Trois mesures l'ont pourtant demolie :
///
///  1. Service DrDXT 2.4.1 demarre par le SCM avec
///     `--control-pipe \\.\pipe\drdxt-presentmon-control`, service Intel ARRETE :
///     il publie `sharedpresentmonsvcnamedpipe-in/-out`, **le nom par defaut**. L'option est
///     parsee et **sans effet sur le nom publie** — elle sert au client, pas au serveur.
///  2. Meme essai avec le binaire **Intel 2.5.1** (service jetable, supprime depuis) : meme
///     resultat, nom par defaut. Monter la version vendorée ne rendrait donc pas l'option
///     operante. (2.5.1 refuse par ailleurs `--intro-nsm` : sa ligne de commande a change.)
///  3. Client 2.4.1 → service Intel 2.5.1, sur le tube par defaut : `PM_STATUS_PIPE_ERROR` (12).
///     Client 2.4.1 → service DrDXT 2.4.1, meme tube : **succes**.
///
/// Le contrôle qui rend ces mesures lisibles : lance EN CONSOLE (`--debug`), le service ne cree
/// jamais de tube de controle, meme sans aucune option. Tous les essais console sur cette question
/// ne prouvent rien ; **seule la mesure par le SCM compte**.
///
/// ══ ce qui en decoule, et la generalite a ne pas ecrire ══
/// Il n'existe qu'un seul nom de tube publiable par ces binaires, et un seul service peut le
/// posseder. Le conflit n'est pas non plus un simple conflit de nom — mesure 3 : le client 2.4.1
/// ne parle pas au service 2.5.1, meme quand il l'atteint.
///
/// **Ce commentaire a conclu « la cohabitation est hors d'atteinte ». C'etait faux**, et la
/// machine l'a dementi le jour meme. La seule conclusion que les mesures 1 a 3 autorisent est :
/// *la paire de versions utilisee par DrDXT ne cohabite pas avec le service Intel de cette
/// machine.* Elles ne disaient rien d'une paire ASSORTIE.
///
/// ══ mesure 4, le meme jour : une paire assortie fonctionne ══
/// Client **2.5.1** contre le service Intel **2.5.1 actif** : `pmOpenSession` = succes, et
/// `pmStartTrackingProcess` sur un vrai processus = succes. Le meme essai avec notre client 2.4.1
/// rend PIPE_ERROR. Le probleme n'est ni le tube, ni la cohabitation : c'est **la version du client
/// vendoré**. Instrument : `tools/pmonpair`.
///
/// Et la montee coute presque rien, headers compares : les **89 valeurs de `PM_METRIC` sont
/// identiques et dans le meme ordre** (2.5.1 en ajoute trois, dont `PM_METRIC_SESSION_START_QPC`),
/// et `PM_QUERY_ELEMENT` est **inchangee**. Le code client n'a rien a changer — seul le binaire
/// vendoré. Voir `docs/PRESENTMON-COEXISTENCE.md` et le gate dans `GATES.md`.
/// </summary>
public static class PresentMonNaming
{
    /// <summary>Le tube de controle. C'est le nom par defaut de PresentMon, et ce n'est pas un
    /// choix : mesure 1 ci-dessus, le serveur publie ce nom quoi qu'on lui passe.
    ///
    /// Il reste nomme ici, en une seule constante, pour deux raisons concretes : le client
    /// l'ouvre explicitement (`pmOpenSessionWithPipe`) au lieu de dependre d'un defaut implicite,
    /// et le jour ou l'API vendorée montera de version, **une seule ligne** decidera a quel
    /// service DrDXT parle.</summary>
    public const string ControlPipe = @"\\.\pipe\sharedpresentmonsvcnamedpipe";

    /// <summary>Les arguments a passer au binaire du service : **aucun**.
    ///
    /// Cette propriete a porte `--control-pipe`, `--intro-nsm` et `--etw-session-name` pendant
    /// exactement une journee. Les mesures ci-dessus ont montre que le premier n'a aucun effet sur
    /// le nom publie ; les deux autres n'ont **jamais ete mesures** comme ayant un effet. Un
    /// produit qui pose des arguments dont il ne sait pas ce qu'ils font se ment a lui-meme, et
    /// invente une difference entre deux installations qui n'en ont aucune.
    ///
    /// La propriete reste — vide — parce que l'installeur et le helper elevé partagent cette
    /// source unique, et parce qu'un test relit les deux ecritures pour qu'elles ne derivent pas.</summary>
    public static string ServiceArguments => string.Empty;
}
