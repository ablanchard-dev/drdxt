namespace DrDxt.Core;

// Obligatoire : les codes littéraux ne doivent jamais être éparpillés ailleurs.
public static class ErrorCodes
{
    public const string DetectionFailed = "DETECTION_FAILED";
    public const string BenchmarkFailed = "BENCHMARK_FAILED";
    public const string RestorePointBlocked = "RESTORE_POINT_BLOCKED";
    public const string ElevationDenied = "ELEVATION_DENIED";
    /// <summary>Le helper ne connaît pas ce bail pour cet appelant (expiré, inverse rejoué). Seul refus qui dise « bail
    /// rendu » : les refus d'appelant passagers restent <see cref="ElevationDenied"/>.</summary>
    public const string LeaseUnknown = "LEASE.UNKNOWN";
    /// <summary>Le helper a rendu l'origine de ce bail lui-même (expiré, inverse réussi). Seul refus qui permette de lever
    /// le journal : un bail simplement inconnu (helper relancé, registre vide) laisse le réglage peut-être posé.</summary>
    public const string LeaseReverted = "LEASE.REVERTED";
    /// <summary>Le bail vient d'expirer et son inverse tourne : ni rendu ni inconnu encore. Passager, le battement continue.</summary>
    public const string LeaseReverting = "LEASE.REVERTING";
    // Le joueur a refusé l'invite Windows. Repris de HelperLauncher pour que
    // l'Application le reconnaisse sans dépendre du helper.
    public const string ElevationDeclined = "ELEVATION_DECLINED";
    // Une écriture d'un levier a été refusée, et le retour arrière des écritures déjà
    // faites aussi : une partie du réglage est encore en place. L'appelant garde son journal (RetourArriere).
    public const string TweakPartiallyApplied = "TWEAK.PARTIALLY_APPLIED";
    // Handshake : un pair s'est connecté mais le handshake était malformé, tronqué,
    // illisible ou portait un marqueur de protocole non pris en charge : une panne de
    // transport ou de protocole, pas un refus d'autorisation. Distinct d'ElevationDenied
    // (réservé à un pair bien formé qui a échoué à l'autorisation de l'appelant). Les deux
    // refusent au moindre doute (aucune clé émise).
    public const string HandshakeProtocolError = "HANDSHAKE_PROTOCOL_ERROR";
    // Lancement à la demande : le helper élevé a été lancé (ou cru lancé) mais son pipe
    // n'a jamais accepté de vraie connexion + handshake dans le délai de démarrage.
    // Distinct d'ELEVATION_DECLINED (UAC annulé par l'utilisateur) : ici un helper lancé
    // n'est pas monté à temps. Propre, à retenter sur une op suivante.
    public const string HelperUnreachable = "HELPER_UNREACHABLE";
    // La requête est partie, puis le helper s'est tu (tué, planté) avant de répondre : l'op a pu s'exécuter ou non.
    // Distinct d'HELPER_UNREACHABLE, où rien n'est parti. Jamais rejouée d'office.
    public const string HelperNoReply = "HELPER_NO_REPLY";
    // La même chose pour une pose notée au journal d'écriture anticipée : si elle est posée, le rejeu la rendra.
    public const string HelperNoReplyJournalisee = "HELPER_NO_REPLY.JOURNALED";
    // Le helper n'a pas de journal d'écriture anticipée (racine non fiable) : une pose qui devait y être notée est refusée.
    public const string JournalIndisponible = "WRITE_AHEAD_JOURNAL_UNAVAILABLE";
    public const string SignatureInvalid = "SIGNATURE_INVALID";
    public const string ManifestTampered = "MANIFEST_TAMPERED";
    // Le manifeste existant ne se relit pas (altere, signe par une autre cle, illisible) :
    // le commit est refuse, rien n'est ecrit, et l'App propose la reprise explicite. Jamais un
    // ecrasement silencieux : il effacerait les seules origines que la desinstallation sait rendre.
    public const string ManifestUnreadable = "MANIFEST_UNREADABLE";
    // La cle de signature de la machine existe mais est inutilisable (illisible, ACL non
    // fiable, ou c'est la cle de dev). Refus nomme ; la meme reprise explicite la met de cote.
    public const string ManifestKeyUnusable = "MANIFEST_KEY_UNUSABLE";
    // Le manifeste signe est ecrit, mais la reference de derive (build, pilote) n'a pas pu etre
    // posee dans le fichier du reconciliateur : la 1re passe notifiera au lieu de reappliquer. Dit, jamais tu.
    public const string ManifestReferenceNotSeeded = "MANIFEST_REFERENCE_NOT_SEEDED";
    public const string GuideLinkTampered = "GUIDE_LINK_TAMPERED";
    public const string UpdateExpired = "UPDATE_EXPIRED";
    public const string OpNotAllowed = "OP_NOT_ALLOWED";
    public const string IoFailure = "IO_FAILURE";

    // Codes de détection
    public const string WmiFail          = "DET.WMI_FAIL";
    public const string DxgiFail         = "DET.DXGI_FAIL";
    public const string NvapiUnavailable = "DET.NVAPI_UNAVAIL";
    public const string AdlxUnavailable  = "DET.ADLX_UNAVAIL";
    public const string NoGpu            = "DET.NO_GPU";
    public const string PowerFail        = "DET.POWER_FAIL";
    public const string RegistryFail     = "DET.REG_FAIL";
    public const string FirmwareFail     = "DET.FIRMWARE_FAIL";

    // Codes de mesure
    public const string PmonUnavailable  = "MEAS.PMON_UNAVAIL";
    public const string CaptureFail      = "MEAS.CAPTURE_FAIL";
    public const string NoFrames         = "MEAS.NO_FRAMES";
    public const string PidInvalid       = "MEAS.PID_INVALID";

    // Ajouts de la colonne PresentMon
    // Processus vivant mais 0 trame captée = l'anti-triche du jeu bloque ETW (distinct d'un service absent).
    public const string NoPresentingFrames   = "MEAS.NO_PRESENTING_FRAMES";
    // Binaire du service non installé (une installation élevée unique requise) ≠ PMON indisponible à l'exécution.
    public const string ServiceNotInstalled = "MEAS.SERVICE_NOT_INSTALLED";

    // Instrument de latence réseau.
    // La demande de mesure est inutilisable (pas d'hôte, nombre de sondes <= 0, délai <= 0)
    // → refus avant tout envoi, jamais de cible par défaut devinée.
    public const string MeasureInvalidInput = "MEAS.INVALID_INPUT";
    // Aucune sonde envoyée (annulé avant la première) → aucune mesure, ce qui n'est
    // pas 100 % de perte. Ne jamais publier un chiffre de perte qu'on n'a pas observé.
    public const string MeasureNoSamples = "MEAS.NO_SAMPLES";
    // Les compteurs de la carte réseau sont illisibles (interface absente, ou disparue en
    // cours de lecture). C'est une absence de lecture, pas « zéro erreur » : une carte qu'on
    // ne peut pas interroger n'est jamais déclarée saine.
    public const string MeasureNicUnreadable = "MEAS.NIC_UNREADABLE";

    // Codes du scan
    public const string NoBaseline       = "SCAN.NO_BASELINE";

    // Détection du stockage
    public const string StorageFail      = "DET.STORAGE_FAIL";

    // Docteur BIOS (socle). Le HardwareInventory/SettingsState passé au conseiller est
    // incohérent (par exemple Memory vide alors qu'on infère XMP)
    // → refus propre, jamais d'exception, jamais de Finding firmware inventé.
    public const string BiosInputInvalid = "BIOS_INPUT_INVALID";

    // Docteur BIOS (contenu du guide). Les données de la bibliothèque sont corrompues :
    // JSON malformé, fabricant ou réglage inconnu, nom exact de réglage vide (le filet de
    // sécurité), ou marqueur d'injection dans une valeur → refus propre (prudence), jamais
    // de rendu de données externes non validées, jamais d'exception.
    public const string BiosTemplateInvalid = "BIOS_TEMPLATE_INVALID";

    // Docteur BIOS (vérification en boucle). Le marqueur sur disque est corrompu : JSON
    // malformé, cibles absentes ou vides, réglage inconnu, ou horodatage hors plage → la
    // boucle refuse d'y voir un vrai changement en attente (échec propre, jamais
    // d'exception). Distinct d'« absent » (pas de marqueur = rien en attente, un Ok qui ne
    // fait rien). Un marqueur corrompu est une panne qu'on montre, jamais crue en silence.
    public const string BiosPendingChangeCorrupt = "BIOS_PENDING_CHANGE_CORRUPT";

    // Docteur BIOS (vérification en boucle). Lire ou écrire le fichier marqueur a
    // rencontré une vraie panne d'E/S (le dossier, le fichier temporaire, le déplacement) →
    // échec propre via Result, frontière protégée, jamais d'exception non gérée.
    public const string BiosPendingChangeIo = "BIOS_PENDING_CHANGE_IO";

    // Mesure memoire. La reference sur disque est illisible (JSON malforme, identifiant de
    // machine vide) : on refuse de s'en servir plutot que de comparer un apres a n'importe quoi.
    // Distinct de "absente", qui n'est pas une erreur : rien n'a encore ete mesure.
    public const string MemoryBaselineCorrupt = "MEMORY_BASELINE_CORRUPT";

    // Mesure memoire. Ecrire ou lire le fichier de reference a bute sur une vraie panne
    // d'entree/sortie : echec propre par Result, jamais d'exception qui remonte.
    public const string MemoryBaselineIo = "MEMORY_BASELINE_IO";

    // Codes d'échec propre de l'allowlist du helper élevé et de l'IPC signé.
    // L'op n'était pas dans l'allowlist fermée, à la feuille près (type inconnu, cible
    // interdite, ou champ hors plage) → refusée avant exécution, jamais lancée.
    public const string NotAllowlisted   = "HELPER.NOT_ALLOWLISTED";
    // La demande privilégiée typée a échoué au contrôle d'intégrité du transport (HMAC) →
    // refus propre, op jamais exécutée. (Distinct de l'ancien
    // SignatureInvalid="SIGNATURE_INVALID" du chemin par enveloppe.)
    public const string HelperSignatureInvalid = "HELPER.SIGNATURE_INVALID";
    // Une op signée et sur l'allowlist a été transmise, mais l'action privilégiée a
    // échoué à l'exécution (par exemple l'écriture SDK ou registre sous-jacente).
    public const string HelperOpFailed   = "HELPER.OP_FAILED";
    /// <summary>Le décalage de tension écrit n'a pas été relu : le BIOS le refuse à l'exécution (Undervolt Protection, verrou).</summary>
    public const string TensionRefusee   = "HELPER.CPU_VOLTAGE_REFUSED";
    /// <summary>Après un refus, un plan déjà posé n'a pas pu être rendu (relu différent) : la tension reste changée jusqu'au
    /// redémarrage, qui la rend.</summary>
    public const string TensionNonRendue = "HELPER.CPU_VOLTAGE_NOT_RESTORED";

    // Sécurité / retour arrière (ISafetyManager).
    // La Restauration du système est coupée et ne peut pas être activée (GPO/SKU/verrouillée)
    // → blocage, jamais d'avance sans point de restauration vérifié.
    public const string RestoreDisabled  = "SAFETY.RESTORE_DISABLED";
    // La Restauration du système est coupée et l'utilisateur a refusé de l'activer → blocage.
    public const string RestoreDeclined  = "SAFETY.RESTORE_DECLINED";
    // La création (ou la vérification) du point de restauration a échoué → blocage, rien d'appliqué.
    public const string RestoreFailed     = "SAFETY.RESTORE_FAILED";
    // Windows a accepté la demande, mais aucun point n'a pu être relu ensuite (pas créé, ou liste illisible).
    public const string RestoreNotVerified = "SAFETY.RESTORE_NOT_VERIFIED";
    // L'OriginalValueJson du TweakSnapshot est malformé ou n'est pas une op typée connue →
    // refus propre du retour arrière (jamais d'exception, jamais d'op inconnue lancée).
    public const string SnapshotInvalid   = "SAFETY.SNAPSHOT_INVALID";

    // Applicabilité du levier / disponibilité du SDK (ITweak).
    // Le levier sort de sa condition matérielle ou sa valeur demandée est hors bornes
    // → il ne s'applique pas à cette machine (on passe proprement, jamais d'écriture).
    public const string TweakNotApplicable = "TWEAK.NOT_APPLICABLE";
    // Le SDK du fabricant ou la lecture du registre, nécessaire pour relever l'origine ou
    // appliquer le changement, est indisponible → échec propre (jamais d'exception), rien d'appliqué.
    public const string TweakSdkUnavailable = "TWEAK.SDK_UNAVAILABLE";
    // L'executable n'a aucun profil pilote a lui (NVAPI_EXECUTABLE_NOT_FOUND) : le pilote
    // lui applique le global. Distinct d'un profil qui existe mais ne se lit pas (TweakSdkUnavailable) :
    // seul ce cas-ci autorise a se rabattre sur le profil global.
    public const string DrsNoGameProfile = "TWEAK.DRS_NO_GAME_PROFILE";
    // Un GPU AMD est présent mais le SDK de réglage ADLX n'est pas disponible dans cette
    // version. Distinct de TweakSdkUnavailable (générique) et de TweakNotApplicable
    // (condition matérielle) : c'est un « à venir » honnête pour le réglage GPU AMD.
    // Jamais de saut silencieux, jamais de faux succès, toujours dire pourquoi.
    public const string GpuAmdTuningUnavailable = "TWEAK.GPU_AMD_TUNING_UNAVAILABLE";
    // Le levier exige l'élévation mais on l'applique sans client élevé.
    public const string TweakElevationRequired = "TWEAK.ELEVATION_REQUIRED";
    // Un levier de réglage GPU (sous-tension / limite de puissance) a été appliqué mais son
    // test de stabilité a vu un blocage, un artefact ou un plantage → il s'est annulé
    // aussitôt et rend ceci. On ne rend jamais Ok quand le GPU est instable.
    public const string TweakUnstable = "TWEAK.UNSTABLE";
    // Le chemin de réglage GPU n'a pas pu être prouvé inscriptible sur cette carte et ce pilote :
    // la sonde de capacité avant application (réécriture de l'origine relevée + relecture)
    // a échoué ou relu une autre valeur. La règle :
    // on ne possède pas le matériel 2015-2026, donc la prise en charge se prouve par sonde,
    // appareil par appareil, jamais d'après un nom d'architecture. Refus → le levier est refusé
    // honnêtement et rien n'est réglé. Distinct de TweakSdkUnavailable (pas de SDK du tout) et
    // de TweakNotApplicable (condition matérielle / valeur hors bornes).
    public const string TweakUnsupportedOnThisGpu = "TWEAK.UNSUPPORTED_ON_THIS_GPU";
    // Un autre outil (logiciel de réglage, profil du fabricant) a déjà posé un décalage sur la carte : DrDXT n'empile pas
    // le sien et ne l'écrase pas. Rien n'est écrit.
    public const string GpuReglageTiers = "TWEAK.GPU_THIRD_PARTY_TUNING";
    // Le décalage courant de la carte (cœur ou mémoire) n'a pas pu être lu : non lu n'est jamais 0, rien n'est écrit.
    public const string GpuDecalageNonLu = "TWEAK.GPU_OFFSET_UNREAD";
    /// <summary>Rendre cette fréquence AMD dépasse la bande : seule la remise à l'usine de la carte y mène, et elle ne part
    /// que sur un geste du joueur (AnnulationParClic). Rien n'a été écrit.</summary>
    public const string GpuRemiseUsineRequise = "TWEAK.GPU_FACTORY_RESET_REQUIRED";

    // Profils d'optimisation par puce.
    // Le HardwareInventory passé au matcher est absent ou incohérent (par exemple null)
    // → refus propre, jamais de profil deviné, jamais d'exception.
    // Le matcher rend quand même la base générique sûre pour une puce reconnue mais sans
    // profil ; ce code est réservé à une entrée inutilisable.
    public const string ProfileInputInvalid = "PROFILE.INPUT_INVALID";

    // Moteur de leviers transactionnel (ITweakEngine).
    // Une étape du lot a échoué à l'application → tout le lot déjà appliqué a été
    // annulé (LIFO). Jamais d'état à moitié appliqué.
    public const string BatchReverted     = "ENGINE.BATCH_REVERTED";
    // La remesure ne montre aucun gain mesurable au-delà des marges cumulées.
    // C'est un état valide et gardé (pas une régression) : dit honnêtement, pas annulé.
    public const string NoMeasurableGain  = "ENGINE.NO_MEASURABLE_GAIN";

    // Un paquet de données (tables) hors du schéma fermé : refusé en entier, les tables en service restent.
    public const string DonneesHorsSchema = "DATA.OUT_OF_SCHEMA";
}
