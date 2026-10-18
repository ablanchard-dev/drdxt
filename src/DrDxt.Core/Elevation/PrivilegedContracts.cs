namespace DrDxt.Core;

// --- Élévation ---

public interface IElevatedClient
{
    Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct);

    /// <summary>Battement : prolonge le bail d'un levier tenu dans l'homme mort du helper
    /// élevé. Une fine enveloppe qui envoie un <see cref="RenewLeaseOp"/> par le même
    /// pipeline IPC que <see cref="ExecuteAsync"/> : elle hérite donc de tout le chemin
    /// signature → nonce → autorisation de l'appelant → liaison → allowlist, et elle est
    /// autorisée par la même identité d'appelant (seul le détenteur du bail peut le renouveler).
    /// Méthode par défaut : chaque implémenteur existant (doublures, stubs, client
    /// indisponible) l'a gratuitement, sans toucher au chemin privilégié.</summary>
    Task<Result> RenewLease(string leaseId, int leaseSeconds, CancellationToken ct)
        => ExecuteAsync(new RenewLeaseOp(leaseId, leaseSeconds), ct);

    /// <summary>Le même canal, sans jamais lancer le helper : pour ce qui part sans geste du joueur (le battement d'un bail).
    /// Un helper absent rend alors HELPER_UNREACHABLE au lieu d'ouvrir une invite Windows ; refusée, elle reviendrait à
    /// chaque battement, en jeu compris. Par défaut le client lui-même (il ne lance rien).</summary>
    IElevatedClient SansRelance => this;

    /// <summary>Envoie une op qui repond au lieu d'agir, et rend ses octets. Même chemin
    /// signé/allowlisté que <see cref="ExecuteAsync"/> ; seul le retour diffère.
    ///
    /// Défaut : echec franc. Un client qui n'a pas de canal de retour de données le dit : il ne
    /// rend pas un tableau vide, qui se lirait comme « le firmware a rendu zéro octet ». Comme
    /// <see cref="RenewLease"/>, c'est une méthode par défaut pour que les implémenteurs
    /// existants (doublures, client indisponible, réconciliateur en processus) héritent du bon
    /// refus sans toucher au chemin privilégié.</summary>
    Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
        => Task.FromResult(Result<byte[]>.Fail(
            ErrorCodes.NotAllowlisted, "This elevated client has no data return channel."));
}

// -----------------------------------------------------------------------
// Contrat typé des ops privilégiées (« feuille exacte » : une feuille exacte par op).
//
// PrivilegedOp est scellé à la frontière : pour demander quoi que ce soit au helper
// élevé, il faut construire l'une des feuilles scellées et fermées ci-dessous.
// Il n'y a aucune feuille générique « écris cette clé de registre » / « lance cette
// commande » : cette primitive interdite, du genre WinRing0, est ce qu'on refuse de bâtir.
//
// Chaque feuille porte des champs typés et validés (pas un blob opaque) : le helper
// la valide à la feuille près (OpAllowlist) avant tout geste privilégié.
// -----------------------------------------------------------------------
public abstract record PrivilegedOp;

// Le test de santé qui ne fait rien. L'ensemble fermé des feuilles est l'allowlist.
public sealed record PingOp : PrivilegedOp;

// --- Feuilles de registre (feuille exacte : ruche + chemin + nom + valeur typée) ---

/// <summary>Pose une seule valeur de registre. Le triplet (ruche, chemin, nom) est la
/// feuille exacte que le helper valide contre son allowlist, jamais un sous-arbre libre.</summary>
public sealed record SetRegistryValueOp(
    RegistryHiveKind Hive,
    string Path,
    string Name,
    RegistryValueType Kind,
    string Value) : PrivilegedOp;

/// <summary>Supprime une seule valeur de registre nommée (pour un retour arrière propre).</summary>
public sealed record DeleteRegistryValueOp(
    RegistryHiveKind Hive,
    string Path,
    string Name) : PrivilegedOp;

// --- Feuilles alimentation et point de restauration ---

/// <summary>Change le plan d'alimentation actif de Windows. Le GUID doit être l'un des
/// GUID de plan connus de Windows (validé par l'allowlist du helper).</summary>
public sealed record SetActivePowerPlanOp(Guid PlanGuid) : PrivilegedOp;

/// <summary>Le mode de boost du processeur (PERFBOOSTMODE) sur secteur, dans un plan, et rien
/// d'autre du plan. Le réglage est fixé par le type : l'appelant ne choisit que le plan et la valeur (0 à 4, bornée
/// par le helper). Le helper réactive le plan s'il est actif (PowerWriteACValueIndex : sans PowerSetActiveScheme, la
/// valeur du plan actif ne prend pas effet).</summary>
public sealed record SetProcessorBoostModeOp(Guid PlanGuid, uint AcValue) : PrivilegedOp;

/// <summary>La limite de puissance du processeur Intel (registre MSR_PKG_POWER_LIMIT, 0x610) écrite
/// par PawnIO. <paramref name="Valeur"/> est le registre entier à écrire, <paramref name="Origine"/> le registre tel qu'il
/// a été lu avant : le helper n'écrit que si le registre porte encore l'origine, et seulement une valeur qui ne diffère
/// de l'origine que par PL1 (bits 15:0), sans verrou, PL1 au plus égale à PL2. L'inverse est la même op, les deux
/// valeurs échangées. Volatil : le BIOS remet sa valeur au redémarrage.</summary>
public sealed record SetCpuPackagePowerLimitOp(ulong Valeur, ulong Origine) : PrivilegedOp;

/// <summary>Le même décalage de tension sur les cœurs et le cache d'un processeur Intel, par la boîte aux lettres OC (MSR 0x150),
/// écrit par le seul helper. Relatif à la tension que le BIOS avait mise à ce démarrage : de 0 (l'origine rendue telle quelle) à
/// −125 mV, jamais au-dessus de l'origine, jamais sous −125 mV au total (<see cref="TensionCpu"/>) ; refusé avec SGX (Plundervolt) ;
/// relu, sinon <see cref="ErrorCodes.TensionRefusee"/>. L'inverse est la même op avec 0. Volatil : rendu au redémarrage.</summary>
public sealed record SetCpuVoltageOffsetOp(int DecalageMv) : PrivilegedOp;

/// <summary>Le mode d'économie d'énergie de la carte sans fil (secteur) dans un plan, et rien d'autre du
/// plan. Le réglage est fixé par le type : l'appelant ne choisit que le plan et la valeur (0 à 3, bornée par le helper).
/// Le helper réactive le plan s'il est actif. La valeur batterie n'est jamais touchée.</summary>
public sealed record SetWirelessPowerSavingOp(Guid PlanGuid, uint AcValue) : PrivilegedOp;

/// <summary>Un programme lancé au démarrage, coupé ou réactivé comme le fait le Gestionnaire des tâches : la valeur au nom
/// du programme sous <c>Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Liste}</c>. Le programme et
/// sa commande ne sont jamais touchés. Le format n'est pas documenté par Microsoft : relevé le 06/10 sur 33 valeurs de la
/// machine de référence, et décrit pareil par la communauté (elevenforum, forums.powershell.org) : 12 octets, 02 en tête
/// pour un programme actif, 03 pour un programme coupé, suivi de l'heure de la coupure (FILETIME, octets 4 à 11).</summary>
/// <param name="Liste"><see cref="Run"/>, <see cref="Run32"/> (machine seulement) ou <see cref="DossierDemarrage"/>.</param>
/// <param name="Nom">Le nom de la valeur Run, ou le nom du fichier du dossier Démarrage (extension comprise).</param>
public sealed record SetStartupApprovalOp(RegistryHiveKind Hive, string Liste, string Nom, bool Actif) : PrivilegedOp
{
    public const string Run = "Run", Run32 = "Run32", DossierDemarrage = "StartupFolder";

    /// <summary>Les 12 octets que le Gestionnaire des tâches écrit.</summary>
    public static byte[] Octets(bool actif, DateTime utc)
    {
        var o = new byte[12];
        o[0] = actif ? (byte)2 : (byte)3;
        if (!actif) BitConverter.GetBytes(utc.ToFileTimeUtc()).CopyTo(o, 4);
        return o;
    }
}

/// <summary>Crée un point de restauration avant l'application d'un lot.</summary>
public sealed record CreateRestorePointOp(string Description) : PrivilegedOp
{
    /// <summary>Le seul nom sous lequel le helper crée un point. Il retire ses anciens
    /// points d'après ce nom exact : un nom libre ferait retirer les points d'un autre logiciel.</summary>
    public const string NomDrDxt = "DrDXT : avant optimisation";

    /// <summary>Le nom des versions d'avant le 06/10/2026 : ces points-là sont aussi les siens.</summary>
    public const string AncienNom = "DrDXT batch";
}

/// <summary>Active la Restauration du système sur le lecteur donné (par exemple « C: ») et
/// réserve un petit espace de clichés pour qu'un point puisse vraiment être créé. Le
/// lecteur est validé à la feuille près par le helper (seul le lecteur système est permis) :
/// jamais d'activation sur un volume quelconque. Sert quand un client a la Restauration
/// coupée : avec son accord explicite, l'app passe par le helper pour que la garantie de
/// retour (un point de restauration vérifiable) tienne avant toute application.</summary>
public sealed record EnableSystemRestoreOp(string DriveLetter) : PrivilegedOp;

// --- Feuilles de réglage GPU (décalage de P-state + limite de puissance, jamais la tension) ---

/// <summary>Applique à un GPU un décalage d'horloge NVAPI cœur/mémoire (MHz). Les bornes
/// sont posées par GpuSafeBounds (Phase E) ; ici le helper ne valide que la structure.</summary>
public sealed record ApplyNvApiClockOffsetOp(
    int GpuIndex,
    int CoreOffsetMhz,
    int MemoryOffsetMhz) : PrivilegedOp;

/// <summary>Applique à un GPU une limite de puissance NVAPI (% du TDP).</summary>
public sealed record ApplyNvApiPowerLimitOp(
    int GpuIndex,
    int PowerLimitPercent) : PrivilegedOp;

/// <summary>Applique un seul réglage du pilote NVIDIA (DRS), par exemple Low Latency Mode.
/// SettingId/Value sont des ids numériques bornés, pas des chaînes libres.</summary>
public sealed record ApplyNvApiDrsSettingOp(
    uint SettingId,
    uint Value,
    string ProfileName) : PrivilegedOp;

/// <summary>Supprime un reglage du profil d'un seul jeu, pour le rendre exactement
/// a ce qu'il etait quand le profil ne le possedait pas (herite, defaut pilote, ou livre par
/// NVIDIA : « deletes a setting or sets it back to predefined value », doc NVAPI). C'est l'inverse
/// d'une ecriture au profil du jeu ; il n'existe que pour le revert.
/// <para><see cref="OrigineAttendue"/> est la provenance lue avant l'ecriture : le helper la
/// relit apres la suppression, et c'est elle (pas la valeur seule) qui prouve le retour.
/// <see cref="ValeurAttendue"/> est la valeur d'avant, rapportee, jamais exigee (le global a pu
/// bouger entre-temps).</para></summary>
public sealed record DeleteNvApiDrsSettingOp(
    uint SettingId,
    string ProfileName,
    DrsValueOrigin OrigineAttendue,
    uint ValeurAttendue) : PrivilegedOp;

/// <summary>Applique une valeur de réglage AMD ADLX (anti-lag / sous-tension / puissance).</summary>
public sealed record ApplyAdlxTuningOp(
    int GpuIndex,
    AdlxTuningKind Kind,
    int IntValue) : PrivilegedOp;

/// <summary>Applique une courbe de ventilateur GPU (NVAPI / ADLX), liste ordonnée de
/// points (temp °C, cycle %). Feuille dédiée : une courbe à plusieurs points ne tient pas
/// dans les ops à un seul IntValue. Le helper la valide à la feuille près (GpuIndex >= 0,
/// chaque point dans la plage, jamais de silence aux points chauds) ; la borne prudente
/// contre la surchauffe est GpuSafeBounds (Phase E), placée devant.</summary>
public sealed record ApplyGpuFanCurveOp(
    int GpuIndex,
    IReadOnlyList<FanCurvePointDto> Points) : PrivilegedOp;

/// <summary>Un point (temp °C, cycle %) d'une courbe de ventilateur sur le fil privilégié.
/// Un simple DTO dans Core, pour que le codec borné sérialise l'op sans dépendre du type
/// FanCurve de la couche Application.</summary>
public sealed record FanCurvePointDto(int TempC, int DutyPct);

/// <summary>Rend les ventilateurs d'une carte à leur contrôle automatique. L'inverse d'une vitesse
/// fixe posée par <see cref="ApplyGpuFanCurveOp"/> (un seul point) ; le helper relit la politique pour le prouver.</summary>
public sealed record RestoreGpuFanAutoOp(int GpuIndex) : PrivilegedOp;

// --- Feuille de contrôle de service (AMD 3D V-Cache optimizer ; service PresentMon) ---

/// <summary>Pilote un service Windows nommé. ServiceName et l'action sont validés à la
/// feuille près (seuls AMD3DVCacheSvc et le service PresentMon sont permis).</summary>
public sealed record RunServiceControlOp(
    string ServiceName,
    ServiceAction Action) : PrivilegedOp;

/// <summary>Installe (et démarre) le service de mesure PresentMon embarqué.
/// Aucun chemin libre : le helper n'installe que le binaire qu'il livre, par son nom.</summary>
public sealed record InstallPresentMonServiceOp : PrivilegedOp;

// --- Feuilles bail / homme mort (chien de garde du retour automatique) ---

/// <summary>Inscrit le retour automatique d'un levier tenu et réversible auprès du chien
/// de garde des baux, en mémoire du helper élevé. Si le contrôleur meurt (ou cesse
/// d'envoyer ses battements), le chien de garde exécute le retour noté à l'échéance :
/// un levier privilégié tenu (décalage d'horloge, limite de puissance GPU) ne reste jamais posé.
///
/// Sécurité : <see cref="RevertOpJson"/> est le JSON du codec d'une feuille PrivilegedOp
/// typée existante (l'inverse déjà produit par le levier d'Application), pas une commande
/// libre. Le chien de garde la relie par le même PrivilegedOpCodec borné et la revalide
/// à la feuille près (OpAllowlist) avant de l'exécuter : aucune nouvelle primitive
/// privilégiée générique. Le bail appartient à l'appelant authentifié (identité du
/// processus) ; lui seul peut le renouveler ou le clore. Échéance =
/// maintenant + <see cref="LeaseSeconds"/> ; le bail vit en mémoire du helper seulement
/// (rien sur disque, ce serait une surface d'attaque).</summary>
/// <summary>Une pose de levier dont l'App n'a pas encore noté l'origine : le journal d'écriture anticipée du helper garde la
/// valeur d'avant de sa cible jusqu'à l'acquit (<see cref="AcquitterEcritureOp"/>). <see cref="PoseJson"/> est le JSON du
/// codec de l'op posée, validée comme elle (OpAllowlist) ; une enveloppe dans une enveloppe est refusée. Seule une pose
/// marquée est journalisée : une annulation, toujours nue, n'est jamais rejouée.</summary>
public sealed record PoseEnAttenteDOrigineOp(string PoseJson) : PrivilegedOp;

/// <summary>L'App a noté l'origine de cette pose : son entrée du journal d'écriture anticipée, et seulement celle de cet
/// appelant, sort du journal. <see cref="PoseeJson"/> est le JSON du codec de l'op posée.</summary>
public sealed record AcquitterEcritureOp(string PoseeJson) : PrivilegedOp;

public sealed record EstablishLeaseOp(
    string LeaseId,
    string RevertOpJson,
    int LeaseSeconds) : PrivilegedOp;

/// <summary>Battement : prolonge un bail existant (maintenant +
/// <see cref="LeaseSeconds"/>) pour que le chien de garde ne parte pas tant que le
/// contrôleur vit. Passe par tout le pipeline IPC, autorisé par le même chemin
/// d'identité que toute op : seul le détenteur du bail (l'appelant authentifié qui l'a
/// ouvert) peut le renouveler ; un autre appelant est refusé (il ne peut ni détourner
/// ni maintenir en vie le bail d'un autre).</summary>
public sealed record RenewLeaseOp(
    string LeaseId,
    int LeaseSeconds) : PrivilegedOp;

// -----------------------------------------------------------------------
// Énumérations fermées d'appui. Définies dans Core (BCL seule, sans dépendance à
// Microsoft.Win32) pour que Core reste neutre et testable sur toute cible. Le
// helper les traduit en vraies énumérations Win32 au moment d'écrire.
// -----------------------------------------------------------------------

/// <summary>Ruche racine du registre. Reflet neutre de Microsoft.Win32.RegistryHive.</summary>
public enum RegistryHiveKind
{
    LocalMachine = 0,
    CurrentUser = 1,
}

/// <summary>Type de valeur de registre. Reflet neutre de Microsoft.Win32.RegistryValueKind,
/// limité aux types que DrDXT écrit.</summary>
public enum RegistryValueType
{
    DWord = 0,
    QWord = 1,
    String = 2,
    /// <summary>REG_MULTI_SZ. La valeur transportee est une seule chaine dont les lignes
    /// sont les entrees (une entree par ligne). Ajoute pour `PagingFiles`, la seule feuille
    /// multi-chaines que DrDXT ecrit, et elle reste soumise a l'allowlist feuille-exacte :
    /// etendre le type n'etend pas la surface, seule une nouvelle feuille le ferait.</summary>
    MultiString = 3,
}

/// <summary>La forme d'une valeur REG_MULTI_SZ telle que l'écriture la pose (Win32RegistryActions : une entrée par ligne,
/// lignes rognées, vides retirées) : la désinstallation compare la valeur posée et la valeur relue sous cette forme.</summary>
public static class MultiTexteDuRegistre
{
    /// <summary>Les entrées : une par ligne, rognées, vides retirées. La seule copie de la règle.</summary>
    public static string[] Entrees(IEnumerable<string> lignes) => lignes.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

    public static string[] Entrees(string valeur) => Entrees(valeur.Split('\n'));

    public static string Canonique(IEnumerable<string> lignes) => string.Join('\n', Entrees(lignes));

    public static string Canonique(string valeur) => Canonique(valeur.Split('\n'));
}

/// <summary>L'ensemble fermé des actions de service que le helper accepte.</summary>
public enum ServiceAction
{
    Install = 0,
    Start = 1,
    Stop = 2,
    Query = 3,
}

/// <summary>L'ensemble fermé des réglages AMD ADLX que DrDXT applique.</summary>
public enum AdlxTuningKind
{
    AntiLag = 0,
    Undervolt = 1,
    PowerLimit = 2,
    /// <summary>FRTC, le plafond d'images du pilote AMD. IntValue &gt; 0 = allumé à cette cible ;
    /// &lt; 0 = éteint, cible rendue à -IntValue (l'inverse exact d'un essai) ; 0 refusé.</summary>
    FrameRateTarget = 3,
    /// <summary>La Vsync du pilote AMD (IADLX3DWaitForVerticalRefresh). IntValue = le mode
    /// ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE : 0 toujours coupée, 1 coupée sauf avis du jeu, 2 activée sauf avis du jeu,
    /// 3 toujours activée.</summary>
    VerticalRefresh = 4,
    /// <summary>La remise à l'usine de tout le réglage de la carte (IADLXGPUTuningServices.ResetToFactory). IntValue = 0.
    /// Un geste du joueur seulement : jamais le retour d'un bail, jamais rejouée au démarrage.</summary>
    RemiseUsine = 5,
}

/// <summary>
/// Demande à Windows d'envoyer le prochain démarrage dans l'écran de setup du firmware, puis redémarre.
///
/// C'est <c>shutdown /r /fw</c> : une fonction documentée de Windows qui pose pour nous le bit 0
/// d'<c>OsIndications</c> de la spec UEFI. DrDXT n'écrit donc aucune variable firmware lui-même :
/// c'est la différence entre suivre la spec et bricoler la NVRAM, et elle compte autant pour un
/// antivirus que pour la règle produit. Écrire le varstore <c>Setup</c> reste interdit ;
/// cette op ne le contourne pas, c'est le
/// seul geste firmware que la spécification prévoit.
///
/// L'op ne prend qu'un délai : il n'y a exprès aucune forme « lance une commande », les
/// arguments sont fixés par le type et l'appelant ne peut rien exprimer d'autre.
/// </summary>
/// <param name="DelaySeconds">
/// Secondes avant le redémarrage, pour que l'UI montre ce qui va se passer et que l'utilisateur
/// puisse renoncer. Borné par l'allowlist : un délai sans borne laisserait un appelant garer un
/// redémarrage loin dans le futur, vécu comme un redémarrage spontané sans cause visible.
/// </param>
public sealed record RebootToFirmwareSetupOp(int DelaySeconds) : PrivilegedOp;

/// <summary>
/// Annule un démarrage firmware en attente (<c>shutdown /a /fw</c>).
///
/// La moitié « annuler », et la raison pour laquelle le voyage peut être proposé : une demande
/// qu'on ne peut pas retirer n'est pas une demande, c'est un engagement pris à la place de l'utilisateur.
/// </summary>
public sealed record CancelPendingFirmwareBootOp : PrivilegedOp;

/// <summary>
/// Lit une variable firmware EFI (<c>GetFirmwareEnvironmentVariableExW</c>), en session élevée.
///
/// À quoi ça sert : aujourd'hui, quand le client revient d'un redémarrage dans le BIOS, l'app ne
/// sait pas s'il a fait le réglage ; elle l'infère (<c>XmpInferencer</c> s'étiquette lui-même
/// « probably »). Lire la variable met fin au silence après reboot, ce qu'attend la boucle de
/// vérification.
///
/// Lecture seule, et c'est structurel : il n'existe aucune op d'écriture firmware dans
/// l'énumération fermée. Écrire au mauvais offset peut rendre une carte non démarrable, sans
/// annulation possible (le seul recours serait physique), et l'offset dépend du couple
/// (modèle × version de BIOS) : aucune API ne le publie.
///
/// Le nom et le GUID sont validés feuille-exacte par l'allowlist du helper : seul l'espace des
/// variables EFI globales est permis, ainsi que les noms fermés des varstores « Setup » (AMI) et
/// « CpuSetup » (Intel), en lecture seulement. Aucune opération n'écrit dans le firmware.
/// </summary>
public sealed record ReadFirmwareVariableOp(string Name, string VendorGuid) : PrivilegedOp;

/// <summary>
/// Plafonne l'envoi de tout le trafic de la machine, sauf celui du jeu, par deux
/// politiques QoS Windows en memoire (ActiveStore : elles disparaissent au redemarrage).
///
/// Mesure sur la machine de reference (upload sature ~720 Mbit/s, A/B alterne x3, 120 pings) :
/// ping p99 48 -> 18 ms avec le plafond. Au repos : rien a gagner. Source de l'API :
/// learn.microsoft.com/powershell/module/netqos/new-netqospolicy. L'exemption du jeu l'emporte
/// sur la politique generale (mesure le 24/09 : 343 -> 839 Mbit/s pour l'exe exempte).
/// </summary>
public sealed record ApplyUploadCapOp(string GameExeName, long CapBitsPerSecond) : PrivilegedOp;

/// <summary>L'inverse exact de <see cref="ApplyUploadCapOp"/> : retire les deux politiques.</summary>
public sealed record RemoveUploadCapOp : PrivilegedOp;

/// <summary>Retient la protection du ping pour le service de démarrage, sous %ProgramData%\DrDxt.
/// Même borne que <see cref="ApplyUploadCapOp"/> : un nom d'exe nu, un plafond dans la plage.</summary>
public sealed record RetainServiceUploadCapOp(string GameExeName, long CapBitsPerSecond) : PrivilegedOp;

/// <summary>L'inverse de <see cref="RetainServiceUploadCapOp"/> : le service ne porte plus rien.</summary>
public sealed record ForgetServiceUploadCapOp : PrivilegedOp;

/// <summary>Met le maintien du service de démarrage en pause, ou le reprend : le fichier `pause-du-maintien` sous
/// %ProgramData%\DrDxt, que le réconciliateur lit. Un booléen seul : ni chemin, ni texte. Ne peut qu'en faire moins.</summary>
public sealed record SetMaintenancePauseOp(bool EnPause) : PrivilegedOp;

/// <summary>
/// Retire un element du magasin BCD de l'entree de demarrage courante (`{current}`).
///
/// Le raisonnement est celui de `BcdInterpretation` : `bcdedit /enum` ne liste que les elements
/// presents, donc en lire un prouve que quelqu'un l'a ecrit. Le retirer rend la decision a Windows,
/// sans avoir a connaitre sa valeur par defaut. Ensemble ferme d'elements (allowlist), aucun ne
/// peut empecher le demarrage : `bootstatuspolicy` ne decide que de l'ecran de recuperation.
/// </summary>
public sealed record DeleteBcdValueOp(string Element) : PrivilegedOp;

/// <summary>L'inverse exact de <see cref="DeleteBcdValueOp"/> : repose la valeur d'origine capturee,
/// sur le meme ensemble ferme d'elements et de valeurs.</summary>
public sealed record SetBcdValueOp(string Element, string Value) : PrivilegedOp;

/// <summary>
/// Le test memoire de Windows au prochain demarrage, une seule fois : `bcdedit /bootsequence {memdiag}`,
/// ce que fait mdsched. Le gestionnaire de demarrage efface la sequence apres l'avoir suivie (Microsoft,
/// « BCDEdit /bootsequence » : « one-time boot sequence »). Sans parametre : l'appelant ne choisit ni
/// l'entree ni l'ordre. Le redemarrage reste au joueur.
/// </summary>
public sealed record ScheduleMemoryDiagnosticOp : PrivilegedOp;

/// <summary>L'inverse de <see cref="ScheduleMemoryDiagnosticOp"/> : retire la sequence du gestionnaire de
/// demarrage avant qu'elle ne serve.</summary>
public sealed record CancelMemoryDiagnosticOp : PrivilegedOp;

/// <summary>Relance un peripherique dont le pilote est en echec : `pnputil /restart-device <InstanceId>` (Microsoft Learn,
/// « PnPUtil command syntax »). Rien a defaire. L'identifiant est celui que Windows a lu (PNPDeviceID), valide par
/// l'allowlist.</summary>
public sealed record RestartDeviceOp(string InstanceId) : PrivilegedOp;

/// <summary>Retire un vieux paquet de pilote d'affichage du magasin de Windows, apres l'avoir exporte sous
/// %ProgramData%\DrDxt : `pnputil /export-driver` puis `/delete-driver`, sans /force. Refuse si le paquet n'est pas de
/// la classe Display ou si l'export echoue.</summary>
public sealed record RemoveDriverPackageOp(string PublishedName) : PrivilegedOp;

/// <summary>L'inverse de <see cref="RemoveDriverPackageOp"/> : remet le paquet exporte (`pnputil /add-driver ... /install`).
/// Le chemin est derive par le helper du nom publie, jamais transmis.</summary>
public sealed record RestoreDriverPackageOp(string PublishedName) : PrivilegedOp;

/// <summary>
/// L'etat du magasin de composants de Windows, tel que `DISM /CheckHealth` le rend :
/// sain, reparable ou non reparable (Microsoft, « Repair a Windows Image »). Lecture seule : aucun
/// parametre, rien n'est repare. DISM exige les droits administrateur, d'ou le helper.
/// Releve sur la machine de reference le 25/09 : « The component store is repairable ».
/// </summary>
public sealed record ReadComponentStoreHealthOp : PrivilegedOp;

/// <summary>
/// Inscrire au manifeste signe. L'App n'a ni la cle ni le droit d'ecrire la racine
/// admin : elle envoie les entrees, le helper eleve les valide (identifiants du catalogue, valeurs
/// bornees, ce qui serait execute doit passer l'allowlist), les fusionne au manifeste existant
/// (premiere origine gagne), signe avec la cle de la machine et ecrit atomiquement.
/// L'App ne tient jamais la cle privee.
/// <c>Retirer</c> ote ces identifiants des deux listes (levier annule). Choix ecrit : le helper ne
/// verifie pas que la valeur d'origine a ete remise avant ; un appelant authentifie (la vraie App, meme compte) peut
/// donc effacer une origine, et la desinstallation rendra moins. Ce n'est pas une escalade (rien n'est execute, et ce
/// meme appelant peut deja ecrire ces reglages par l'allowlist), mais une origine HKLM n'est plus immuable.
/// </summary>
/// <param name="Tweaks">Corrections au redemarrage : le reconciliateur les reaffirme.</param>
/// <param name="RevertOnly">Origines des leviers gardes : seule la desinstallation les rend.</param>
/// <param name="RepartirDUnManifesteVide">La voie de reprise, explicite : quand le manifeste existant
/// est illisible (ou la cle inutilisable), il est mis de cote (jamais efface) et un manifeste neuf
/// commence. Refuse sur un manifeste lisible : on ne jette jamais des origines qu'on sait relire.</param>
public sealed record CommitManifestOp(
    IReadOnlyList<AppliedTweak> Tweaks,
    IReadOnlyList<AppliedTweak> RevertOnly,
    bool RepartirDUnManifesteVide = false,
    IReadOnlyList<string>? Retirer = null) : PrivilegedOp;

/// <summary>GUID documentés des plans d'alimentation Windows (les seuls que l'allowlist accepte).</summary>
public static class KnownPowerPlans
{
    public static readonly Guid HighPerformance = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid Balanced        = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid Ultimate        = Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");
    /// <summary>« Économie d'énergie » : DrDXT ne le pose jamais de lui-même ; il le rend quand le joueur annule
    /// fixit.plan_economie, qui l'avait quitté.</summary>
    public static readonly Guid PowerSaver      = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");

    public static bool IsKnown(Guid guid) =>
        guid == HighPerformance || guid == Balanced || guid == Ultimate || guid == PowerSaver;
}
