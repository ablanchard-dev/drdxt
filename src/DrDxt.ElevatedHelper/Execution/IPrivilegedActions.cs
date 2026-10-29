using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Coutures d'action minces de l'exécuteur privilégié : l'aiguillage op typée → action Windows se teste avec des faux ;
// l'appel natif (Registry, powrprof, srclient, ServiceController) est prouvé en réel par le m2smoke. Chaque méthode
// rend un Result et ne lève jamais (échec emballé en HELPER.OP_FAILED). Les arguments sont déjà validés par
// l'allowlist de TypedPrivilegedServer ; les implémentations ne touchent quand même que la feuille exacte donnée
// (défense en profondeur).
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

    /// <summary>Active la Restauration du système sur le lecteur donné (lecteur système seulement, validé par
    /// l'allowlist) et réserve un peu de shadow storage pour qu'un point puisse être créé. Ne lève jamais.</summary>
    Result EnableSystemRestore(string driveLetter);
}

/// <summary>Pilotage des services Windows (démarrer / arrêter / interroger) et installation
/// bornée du service PresentMon.</summary>
public interface IServiceActions
{
    Result Control(string serviceName, ServiceAction action);
    Result InstallPresentMonService();
}

/// <summary>Réglages du pilote NVIDIA (DRS) : la surface NVAPI publique, sans tension ni horloge. Le settingId est
/// validé par l'allowlist (ensemble fermé). Sans NVIDIA, chaque appel rend un Result.Fail, jamais d'exception.</summary>
public interface INvApiDrsActions
{
    Result SetGlobalSetting(uint settingId, uint value);

    /// <summary>
    /// Ecrit un reglage dans le profil de cet executable : la hierarchie du driver est Application &gt; Global courant
    /// &gt; Base, une ecriture globale peut ne jamais atteindre un jeu qui a son propre profil. Echoue quand
    /// l'executable n'a pas de profil : on n'en cree pas, on ne se rabat pas en silence sur le profil de base.
    /// </summary>
    Result SetSettingForApplication(string applicationPath, uint settingId, uint value);

    /// <summary>
    /// Rend au profil de cet executable un reglage qu'il ne possedait pas. Succes seulement si la relecture (session
    /// neuve) montre la provenance attendue : la provenance, pas la valeur, prouve le retour.
    /// </summary>
    Result DeleteSettingForApplication(string applicationPath, uint settingId, DrsValueOrigin origineAttendue);
}

/// <summary>NVAPI privée, risquée, rétro-ingéniée, de la classe de MSI Afterburner (recette §A/§B) : décalage d'horloge
/// cœur P0/Graphics (SetPstates20) et cible de puissance (ClientPowerPoliciesSetStatus). Règle d'or GET puis SET : lire
/// la struct actuelle, ne modifier que le champ visé, jamais une struct construite à zéro. Les lectures capturent le
/// vrai original avant d'appliquer. Sans NVIDIA ou sans id de fonction privée exporté : HELPER.OP_FAILED, jamais
/// d'exception à travers l'IPC. GpuIndex = index des GPU physiques énumérés (>= 0, validé par l'allowlist).</summary>
public interface INvApiTuningActions
{
    /// <summary>Lit le décalage d'horloge cœur P0/Graphics appliqué (MHz) d'un GPU.
    /// Indisponible / pas de NVIDIA → Fail(HELPER.OP_FAILED). Ne lève jamais.</summary>
    Result<int> GetCoreClockOffsetMhz(int gpuIndex);

    /// <summary>Lit le décalage d'horloge mémoire appliqué (MHz) d'un GPU, pour comparer le champ mémoire d'une op à la
    /// carte ; jamais écrit. Non branché → Fail(HELPER.OP_FAILED). Ne lève jamais.</summary>
    Result<int> GetMemoryClockOffsetMhz(int gpuIndex) =>
        Result<int>.Fail(ErrorCodes.HelperOpFailed, "Memory clock offset read is not wired in this executor.");

    /// <summary>Lit la limite de puissance appliquée (% du TDP) d'un GPU.
    /// Indisponible / pas de NVIDIA → Fail(HELPER.OP_FAILED). Ne lève jamais.</summary>
    Result<int> GetPowerLimitPercent(int gpuIndex);


    /// <summary>GET puis SET du delta d'horloge cœur P0/Graphics : seule l'entrée graphique P0 change.
    /// Indisponible / pas d'id privé → Fail(HELPER.OP_FAILED).</summary>
    Result SetCoreClockOffset(int gpuIndex, int offsetMHz);

    /// <summary>GET puis SET de la limite de puissance (% du TDP), bornée à la plage PCM [min, max] du pilote : seule
    /// la cible de entry[0] change. Indisponible → Fail(HELPER.OP_FAILED).</summary>
    Result SetPowerLimitPercent(int gpuIndex, float pct);
}

/// <summary>
/// Envoyer le prochain démarrage dans le setup firmware, et retirer cette demande, par <c>shutdown.exe</c>
/// (<c>/r /fw</c>, <c>/a /fw</c>), l'outil documenté qui possède le bit <c>OsIndications</c>. DrDXT n'écrit aucune
/// variable firmware : aucune frontière NVRAM où se tromper, rien qu'un antivirus lirait comme une écriture firmware.
/// </summary>
public interface IFirmwareBootActions
{
    /// <summary>Demande « le prochain démarrage va au setup firmware » et redémarre après le délai donné.</summary>
    Result RequestFirmwareSetupReboot(int delaySeconds);

    /// <summary>Retire un démarrage firmware en attente. Sans risque si rien n'est en attente.</summary>
    Result CancelPendingFirmwareBoot();

    /// <summary>Lit une seule variable firmware EFI ; exprès aucun pendant en écriture. L'erreur win32 1314 dit que le
    /// privilège élevé manque, pas que le firmware a refusé.</summary>
    Result<byte[]> ReadFirmwareVariable(string name, string vendorGuid);
}

/// <summary>
/// Le plafond d'envoi QoS (politiques Windows en memoire, ActiveStore).
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
