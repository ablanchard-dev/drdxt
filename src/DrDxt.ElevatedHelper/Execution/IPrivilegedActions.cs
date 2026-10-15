using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Coutures d'action internes et minces pour le vrai exécuteur privilégié.
//
// Sécurité / testabilité : la logique d'aiguillage des ops (quelle op typée → quelle action
// Windows, ops GPU refusées proprement) est la partie qui mérite des tests unitaires : elle est
// donc isolée derrière ces coutures. Un test unitaire injecte des faux et vérifie que la bonne
// action a tourné avec les bons arguments (déjà passés par l'allowlist) ; l'appel natif réel
// (Microsoft.Win32.Registry, powrprof, srclient, ServiceController) est la frontière que prouve
// le m2smoke en réel, jamais exercée en test unitaire.
//
// Chaque méthode rend un Result et ne doit jamais lever : l'exécuteur emballe tout échec en
// HELPER.OP_FAILED. Les arguments reçus sont déjà validés par l'allowlist dans
// TypedPrivilegedServer ; les vraies implémentations ne touchent quand même que la feuille exacte
// qu'on leur donne (défense en profondeur).
// -----------------------------------------------------------------------

/// <summary>Écriture / suppression dans le registre sur la feuille exacte (ruche, chemin, nom).</summary>
public interface IRegistryActions
{
    Result SetValue(RegistryHiveKind hive, string path, string name, RegistryValueType kind, string value);
    Result DeleteValue(RegistryHiveKind hive, string path, string name);

    /// <summary>Un programme au démarrage coupé ou réactivé (StartupApproved), seulement s'il est bien listé. Par défaut : refusé.</summary>
    Result SetStartupApproval(RegistryHiveKind hive, string liste, string nom, bool actif) =>
        Result.Fail(ErrorCodes.HelperOpFailed, "Startup approval write is not wired in this executor.");
}

/// <summary>Changement du plan d'alimentation actif (PowerSetActiveScheme).</summary>
public interface IPowerPlanActions
{
    Result SetActivePlan(Guid planGuid);

    /// <summary>PERFBOOSTMODE sur secteur dans ce plan, puis le plan réactivé s'il est actif. Par
    /// défaut : refusé, pour les exécutants qui ne savent pas l'écrire.</summary>
    Result SetBoostModeAc(Guid planGuid, uint acValue) =>
        Result.Fail(ErrorCodes.HelperOpFailed, "Boost mode write is not wired in this executor.");

    /// <summary>L'économie d'énergie de la carte sans fil sur secteur dans ce plan, puis le plan réactivé
    /// s'il est actif. Par défaut : refusé.</summary>
    Result SetWirelessPowerSavingAc(Guid planGuid, uint acValue) =>
        Result.Fail(ErrorCodes.HelperOpFailed, "Wireless power saving write is not wired in this executor.");
}

/// <summary>Création de point de restauration (SRSetRestorePoint / WMI) et activation de la
/// Restauration du système pour le lecteur système (Enable-ComputerRestore + shadow storage).</summary>
public interface IRestorePointActions
{
    Result CreatePoint(string description);

    /// <summary>Active la Restauration du système sur le lecteur donné (déjà validé par l'allowlist,
    /// lecteur système seulement) et réserve un petit espace de shadow storage pour qu'un point
    /// puisse vraiment être créé. Rend un Result ; ne doit jamais lever.</summary>
    Result EnableSystemRestore(string driveLetter);
}

/// <summary>Pilotage des services Windows (démarrer / arrêter / interroger) et installation
/// bornée du service PresentMon.</summary>
public interface IServiceActions
{
    Result Control(string serviceName, ServiceAction action);
    Result InstallPresentMonService();
}

/// <summary>Couture d'écriture des réglages du pilote NVIDIA (DRS) : la surface NVAPI publique et
/// sûre (aucune écriture de tension ni d'horloge). Applique un seul réglage global du pilote par
/// la séquence de session DRS. Le settingId est déjà validé par l'allowlist (ensemble fermé). Sur
/// une machine AMD / sans NVIDIA, chaque appel rend un Result.Fail propre, jamais d'exception.</summary>
public interface INvApiDrsActions
{
    Result SetGlobalSetting(uint settingId, uint value);

    /// <summary>
    /// Ecrit un reglage dans le profil de cet executable. La hierarchie du driver est
    /// Application &gt; Global courant &gt; Base : une ecriture globale peut ne jamais atteindre un
    /// jeu qui possede son propre profil, et le produit annoncerait alors un reglage applique
    /// que le jeu ne verrait pas.
    ///
    /// Echoue proprement quand l'executable n'a pas de profil : on n'en cree pas, et on ne se
    /// rabat pas en silence sur le profil de base.
    /// </summary>
    Result SetSettingForApplication(string applicationPath, uint settingId, uint value);

    /// <summary>
    /// Rend au profil de cet executable un reglage qu'il ne possedait pas.
    /// Succes uniquement si la relecture (session neuve) montre la provenance attendue : c'est la
    /// provenance, pas la valeur, qui prouve que le profil est redevenu ce qu'il etait.
    /// </summary>
    Result DeleteSettingForApplication(string applicationPath, uint settingId, DrsValueOrigin origineAttendue);
}

// -----------------------------------------------------------------------
// INvApiTuningActions : la couture d'écriture NVAPI privée, risquée.
//
// C'est la surface rétro-ingéniée, de la classe de MSI Afterburner (recette §A/§B) :
//   * SetCoreClockOffset : un delta de fréquence P0/Graphics (SetPstates20),
//   * SetPowerLimitPercent : la cible de puissance (ClientPowerPoliciesSetStatus).
// Les deux suivent la règle d'or GET puis SET : lire la struct actuelle, ne modifier que le
// seul champ visé (horloge graphique P0 / cible de puissance entry[0]), la réécrire. Jamais une
// struct d'écriture construite à zéro. Les méthodes de lecture (GetCoreClockOffsetMhz /
// GetPowerLimitPercent) exposent la valeur actuelle pour qu'un levier capture le vrai original
// avant d'appliquer (retour fidèle).
//
// Chaque méthode rend un Result et ne lève jamais à travers l'IPC. Sur une machine AMD / sans
// NVIDIA, ou si l'id de fonction privée n'est pas exporté par le pilote installé, l'appel échoue
// proprement (HELPER.OP_FAILED). GpuIndex est l'index dans la liste énumérée des GPU physiques
// (déjà validé >= 0 par l'allowlist).
// -----------------------------------------------------------------------

/// <summary>Couture de réglage NVAPI privée, risquée : écritures du décalage d'horloge cœur P0 +
/// de la limite de puissance (GET puis SET), avec les lectures assorties pour capturer l'original.
/// Échec propre sur une machine AMD / sans NVIDIA ou sans id de fonction privée : ne lève jamais.</summary>
public interface INvApiTuningActions
{
    /// <summary>Lit le décalage d'horloge cœur P0/Graphics appliqué (MHz) d'un GPU.
    /// Indisponible / pas de NVIDIA → Fail(HELPER.OP_FAILED). Ne lève jamais.</summary>
    Result<int> GetCoreClockOffsetMhz(int gpuIndex);

    /// <summary>Lit la limite de puissance appliquée (% du TDP) d'un GPU.
    /// Indisponible / pas de NVIDIA → Fail(HELPER.OP_FAILED). Ne lève jamais.</summary>
    Result<int> GetPowerLimitPercent(int gpuIndex);


    /// <summary>Écriture GET puis SET du delta de fréquence d'horloge cœur P0/Graphics
    /// (offsetMHz). Ne modifie que l'entrée d'horloge graphique P0 ; ne construit jamais la struct
    /// d'écriture à zéro. Indisponible / pas d'id privé → Fail(HELPER.OP_FAILED).</summary>
    Result SetCoreClockOffset(int gpuIndex, int offsetMHz);

    /// <summary>Écriture GET puis SET de la limite de puissance (% du TDP), bornée à la plage PCM
    /// [min, max] annoncée par le pilote. Ne modifie que la cible de puissance de entry[0] ;
    /// ne construit jamais la struct d'écriture à zéro. Indisponible → Fail(HELPER.OP_FAILED).</summary>
    Result SetPowerLimitPercent(int gpuIndex, float pct);
}

/// <summary>
/// La couture de démarrage firmware : demander à Windows d'envoyer le prochain démarrage dans
/// l'interface de setup du firmware, et retirer cette demande.
///
/// Les deux passent par <c>shutdown.exe</c> (<c>/r /fw</c> et <c>/a /fw</c>), l'outil documenté
/// qui possède le bit <c>OsIndications</c>. DrDXT n'écrit aucune variable firmware lui-même :
/// aucune frontière NVRAM où se tromper, rien qu'un antivirus puisse lire comme une écriture
/// firmware. La couture existe pour simuler l'appel natif en test, comme toute autre action ici.
/// </summary>
public interface IFirmwareBootActions
{
    /// <summary>Demande « le prochain démarrage va au setup firmware » et redémarre après le délai donné.</summary>
    Result RequestFirmwareSetupReboot(int delaySeconds);

    /// <summary>Retire un démarrage firmware en attente. Sans risque si rien n'est en attente.</summary>
    Result CancelPendingFirmwareBoot();

    /// <summary>Lit une seule variable firmware EFI. Lecture seule par construction : il n'y a
    /// exprès aucun pendant en écriture sur cette interface. Un échec de lecture ne prouve pas que le
    /// firmware a refusé : l'erreur win32 1314 signifie que le privilège élevé manque, ce qui ne conclut rien.</summary>
    Result<byte[]> ReadFirmwareVariable(string name, string vendorGuid);
}

/// <summary>
/// Le plafond d'envoi QoS (politiques Windows en memoire, ActiveStore). La couture
/// existe pour que l'appel natif soit simule dans les tests, comme toutes les actions ici.
/// </summary>
public interface INetworkQosActions
{
    /// <summary>Pose (ou remplace) les deux politiques, puis les relit : echec si elles ne sont
    /// pas la avec les bonnes valeurs.</summary>
    Result ApplyUploadCap(string gameExeName, long capBitsPerSecond);

    /// <summary>Retire les deux politiques et verifie qu'il n'en reste aucune. Idempotent.</summary>
    Result RemoveUploadCap();
}

/// <summary>Les gestes pnputil : relancer un peripherique, retirer un vieux pilote d'affichage exporte, le remettre.</summary>
public interface IPnpActions
{
    Result RestartDevice(string instanceId);
    Result RemoveDriverPackage(string publishedName);
    Result RestoreDriverPackage(string publishedName);
}

/// <summary>Ecrire un element BCD de l'entree courante, et le relire.</summary>
public interface IBcdActions
{
    Result DeleteValue(string element);
    Result SetValue(string element, string value);

    /// <summary>Le test memoire de Windows au prochain demarrage, une seule fois, relu.</summary>
    Result ScheduleMemoryDiagnostic();

    /// <summary>Retire la sequence posee par <see cref="ScheduleMemoryDiagnostic"/> ; rien en attente est l'etat voulu.</summary>
    Result CancelMemoryDiagnostic();
}

/// <summary>La limite de puissance du processeur Intel (MSR 0x610) par PawnIO. Par défaut : refusé,
/// pour les exécutants qui ne savent pas l'écrire (le service de démarrage, le banc).</summary>
public interface ICpuPowerActions
{
    Result SetPackagePowerLimit(ulong valeur, ulong origine) =>
        Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit write is not wired in this executor.");

    /// <summary>Le décalage de tension des cœurs et du cache. Par défaut : refusé.</summary>
    Result SetVoltageOffset(int decalageMv) =>
        Result.Fail(ErrorCodes.HelperOpFailed, "CPU voltage offset write is not wired in this executor.");
}

/// <summary>Le ventilateur d'une carte en mode Expert. Poser ne fait que monter, et se relit ;
/// rendre se prouve par la politique relue (voir <see cref="NvmlVentilateurActions"/>).</summary>
public interface IVentilateurActions
{
    Result Poser(int gpuIndex, int pourcent);
    Result Rendre(int gpuIndex);
}
