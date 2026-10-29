namespace DrDxt.Core;

// --- P0 Détection ---

public enum ChassisType { Desktop, Laptop, Handheld, Unknown }

public enum TriState { On, Off, Unknown }

public record CpuInfo(string Model, string Vendor, int Cores, int Threads, bool IsX3D, int CcdCount);

public record GpuInfo(string Model, string Vendor, long VramBytes, string DriverVersion, TriState ReBar);

// Les quatre derniers champs sont optionnels, en queue. 0 / null = « jamais lu », jamais « zero volt » ni « zero rang ».
public record MemoryModule(
    string PartNumber,
    string Manufacturer,
    int ConfiguredMhz,
    int RatedMhz,
    // SMBIOS Memory Device Type. 26 = DDR4 (mesure sur la machine de reference), 34 = DDR5
    // (valeur de la specification, pas verifiee ici). 0 = non lu.
    int SmbiosMemoryType = 0,
    // Rangs par module (SMBIOS Type 17 Attributes, bits 3:0) : le dual rank change le plafond de frequence. 0 = non lu.
    int Ranks = 0,
    // Tension configuree en mV. DDR4 JEDEC = 1200, DDR5 = 1100 : une tension nettement au-dessus
    // prouve qu'un profil XMP/EXPO est charge, independamment de la frequence. 0 = non lu.
    int ConfiguredVoltageMv = 0,
    // Emplacement physique (« Controller0-DIMMA2 ») : ou la barrette est branchee, pas le mode du controleur. Deux
    // controleurs ne prouvent pas le double canal (seul un debit au-dessus du plafond d'un canal, voir MemoryInference).
    // null = non lu.
    string? DeviceLocator = null);

public record MotherboardInfo(string Manufacturer, string Product);

public record BiosInfo(string Version, DateTimeOffset? ReleaseDate, bool IsUefi);

public record HardwareInventory(
    CpuInfo Cpu,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<MemoryModule> Memory,
    MotherboardInfo Board,
    BiosInfo Bios,
    ChassisType Chassis,
    // Les emplacements mémoire de la carte (SMBIOS type 16). 0 = non lu.
    int MemorySlots = 0);

public enum RestartVerdict
{
    /// <summary>Rien n'attend de redémarrage ; un changement à l'essai, s'il y en a un, est en vigueur.</summary>
    NoRestartPending,

    /// <summary>Un redémarrage est dû. Toute mesure prise maintenant décrit la mauvaise configuration.</summary>
    RestartRequired,

    /// <summary>Impossible de conclure. <see cref="RestartState.Reason"/> dit ce qui manque ou gêne.</summary>
    Indeterminate,
}

/// <param name="Reason">Le pourquoi en langage simple, montrable tel quel à un joueur.</param>
public readonly record struct RestartState(RestartVerdict Verdict, string Reason);

public enum DrsValueOrigin
{
    /// <summary>Posé sur ce profil, pas livré par le pilote : un humain l'a changé.</summary>
    SetByUser,

    /// <summary>Posé sur ce profil, mais livré par NVIDIA avec le pilote : une valeur prédéfinie,
    /// le choix de personne.</summary>
    ShippedWithTheDriver,

    /// <summary>Rapporté depuis le profil global : ce profil ne possède rien ici.</summary>
    InheritedFromGlobalProfile,

    /// <summary>Rapporté depuis le profil de base : ce profil ne possède rien ici.</summary>
    InheritedFromBaseProfile,

    /// <summary>Le défaut du pilote, posé nulle part.</summary>
    DriverDefault,

    /// <summary>Un emplacement rapporté par le pilote qu'on ne reconnaît pas. Jamais réduit à
    /// une réponse sûre.</summary>
    Unknown,
}

/// <param name="IsYourOwnChange">Vrai seulement pour <see cref="DrsValueOrigin.SetByUser"/> : le seul
/// cas où il a du sens d'envoyer quelqu'un vers ce profil.</param>
/// <param name="Reason">Le pourquoi en langage simple, montrable tel quel.</param>
public readonly record struct DrsOriginState(DrsValueOrigin Origin, bool IsYourOwnChange, string Reason);

public enum FrameCapVerdict
{
    /// <summary>Le plafond est la valeur basse latence de l'écran du jeu (fréquence - 3).</summary>
    Coherent,

    /// <summary>Le plafond est la valeur basse latence d'un écran branché mais pas principal. Calculé
    /// sur le mauvais écran, il coûte des images sur celui où l'on joue.</summary>
    ComputedOnAnotherDisplay,

    /// <summary>Le plafond mord sous la fréquence principale mais ne suit la règle d'aucun écran.
    /// Rapporté sans nommer une cause qu'on n'a pas établie.</summary>
    CapsBelowPrimary,

    /// <summary>0 (« off » du pilote) ou une valeur égale ou supérieure à la fréquence principale : il ne mord jamais.</summary>
    NotCapping,

    /// <summary>Pas assez lu pour conclure. Jamais un certificat de bonne santé.</summary>
    Indeterminate,
}

/// <param name="Reason">Le pourquoi en langage simple, montrable tel quel à un joueur.</param>
/// <param name="Origin">Qui a posé le plafond, quand on a interrogé le pilote. null = non lu, jamais « personne ».
/// Informatif : l'origine ne change pas le verdict, un plafond coûte des images.</param>
public readonly record struct FrameCapState(
    FrameCapVerdict Verdict, string Reason, DrsOriginState? Origin = null);

public record SettingsState(
    TriState XmpExpo,
    TriState ReBar,
    string PowerPlanGuid,
    TriState Hags,
    // TriState et non bool : un bool ne sait pas dire « illisible », et une lecture ratee deviendrait « Game Mode
    // desactive », a reparer.
    TriState GameMode,
    // null = le parking n'a pas pu etre lu, ou aucun jeu n'etait ouvert (hors jeu, il ne dit rien du routage X3D) ; liste
    // vide = lu, aucun coeur parque. Les confondre fait proposer une ecriture sur une machine saine.
    IReadOnlyList<int>? ParkedCoreIndexes,
    TriState Vrr,
    // VRR coupé n'est un déficit que sur un écran compatible confirmé (On) ; Unknown = occasion neutre (0 pénalité).
    // Facultatif, en dernier.
    TriState VrrCapableDisplay = TriState.Unknown,
    // Fiabilité de la RAM : fichier d'échange présent (On) / désactivé (Off) / illisible (Unknown).
    // Off est un déficit de fiabilité (OOM, gel), pas une occasion de « +FPS ». Facultatif, en dernier.
    TriState PagefileConfigured = TriState.Unknown,
    // Reseau : cartes physiques connectees et leurs mots-cles NDIS. null = jamais lu ; liste vide = lu, aucune carte
    // connectee. Facultatif, en dernier.
    IReadOnlyList<NetworkAdapterState>? NetworkAdapters = null,
    // La grandeur dure derriere XmpExpo, une inference (« probably » selon XmpInferencer) immobile sur un kit qui depasse
    // le socle JEDEC dans les deux etats : la boucle de verification a besoin du chiffre (2133 -> 3800). 0 = jamais lu.
    int MemoryConfiguredMhz = 0,
    // CSM, illisible directement : Legacy le prouve actif (On) ; UEFI ne prouve pas l'inverse (Unknown).
    TriState Csm = TriState.Unknown,
    // État de redémarrage : une mesure prise quand Windows doit redémarrer décrit la mauvaise
    // configuration. null = jamais calculé (les anciennes constructions compilent toujours).
    RestartState? Restart = null,
    // Plafond d'images : calculé sur le mauvais écran, il coûte des images à chaque session. null = jamais calculé.
    FrameCapState? FrameCap = null,
    // L'ecran tourne-t-il sous sa frequence maximale ? Le levier le plus gratuit : un panneau 200 Hz pilote a 143 Hz jette
    // 28 % des images que le GPU rend deja. null = jamais calcule (aucune sonde d'affichage), pas « rien a signaler ».
    RefreshHeadroom? DisplayRefresh = null,
    // Les degats deja infliges : le plus souvent, un defaut Windows qu'un « optimiseur » precedent a casse. Unknown =
    // pas lu, jamais « tout va bien ».
    /// <summary>SysMain encore actif, ou desactive par quelqu'un.</summary>
    TriState SysMain = TriState.Unknown,
    /// <summary>MMCSS intact, ou coupe par `SystemResponsiveness` a 100 (sous 10, Windows ramene a 20 : sans effet).</summary>
    TriState MmcssIntact = TriState.Unknown,
    // LR : les redemarrages sans arret propre (`Kernel-Power 41`), plus recents d'abord. null = jamais lu ; liste vide =
    // lu, aucun. Pas un compte de plantages : une extinction au bouton ou a la prise se logue a l'identique, et tout
    // lecteur de ce champ porte cette reserve.
    IReadOnlyList<DateTimeOffset>? UncleanShutdowns = null,
    // Les incidents datés de la garde de stabilité (arrêts brutaux classés, écrans bleus, vidages, erreurs matérielles
    // graves, hausses d'arrêts dangereux du SSD) : la même source que la garde Curve Optimizer. null = une source non lue,
    // ou lecteur non branché ; une garde le lit comme « instable », jamais comme « stable ».
    IReadOnlyList<DateTimeOffset>? IncidentsDeStabilite = null,
    // Le nombre de canaux memoire que la mesure prouve : un canal plafonne a MT/s x 8 octets, lire au-dessus en prouve
    // plusieurs, meme quand la carte nomme mal ses emplacements (`MemoryTopologyAdvisor` ne sait pas). 0 = jamais mesure,
    // 1 = rien de prouve ; ni l'un ni l'autre ne veut dire « simple canal » (un seul fil ne sature pas forcement deux
    // canaux). Seul >= 2 ferme une inconnue.
    int MemoryProvenChannels = 0,
    // Un seul canal probable, dit par la mesure multi-fil (au moins deux barrettes, et plusieurs fils sous le plafond
    // d'un canal). La seule conclusion vers le bas que DrDXT tire du debit.
    bool MemoryUnSeulCanalMesure = false,
    // La frequence de chaque ecran attache : des frequences differentes decident de la consommation au repos. null =
    // jamais lu ; un 0 = une lecture ratee, jamais un ecran a 0 Hz.
    IReadOnlyList<int>? AttachedRefreshHz = null,
    // Le mode de gestion d'alimentation du pilote NVIDIA (`PREFERRED_PSTATE`) : On = « Prefer maximum performance »,
    // Off = « Adaptive » (le defaut), Unknown = non lu, sans candidat. `latency.prefer_max_perf` doit savoir si la cible
    // est deja en place (mesure le 10/09/2026 sur la machine de reference : `PREFERRED_PSTATE = 1`), sinon il jugerait un
    // placebo.
    TriState NvidiaPreferMaxPerf = TriState.Unknown,
    // D'ou vient cette valeur (null = non lue) et a quel scope elle a ete lue :
    // le chemin du jeu, ou null = profil global (jeu inconnu, ou jeu sans profil propre).
    // Mesure du 10/09 : sur cod.exe la valeur est heritee du global, le jeu ne la possede pas.
    DrsOriginState? NvidiaPreferMaxPerfOrigin = null,
    string? NvidiaDrsScope = null,
    // HAGS tel que Windows l'applique, lu par D3DKMT (KMTQAITYPE_WDDM_2_7_CAPS) sur l'adaptateur qui affiche, pas par
    // `HwSchMode` (mesure du 10/09 : cle absente, HAGS actif, 0x0B). Structure « reserved for system use » : lue de bonne
    // foi, jamais ecrite. Unknown = non lu, non supporte, ou adaptateurs d'affichage qui se contredisent.
    TriState HagsEffective = TriState.Unknown,
    // Le goulot mesure par le scan (`BottleneckClassifier`), pose par l'App avant la
    // selection des leviers. null = aucune mesure en jeu : aucun levier qui en depend ne s'arme.
    BottleneckState? GoulotMesure = null,
    // Les reglages lus dans la NVRAM du BIOS (NvramBiosSettingsReader), recoupes avec Windows : XmpExpo/ReBar/Csm en
    // viennent alors. null = rien de lu, les champs gardent leur inference.
    IReadOnlyList<BiosSettingReading>? BiosLu = null,
    // Les heures d'activité de Windows Update (null = non lues) et les parties relevées
    // (null = relevé non branché ; vide = aucune partie). Le levier des heures d'activité ne s'arme que sur les deux lus.
    HeuresActivesLues? HeuresActives = null,
    IReadOnlyList<PlageDeJeu>? HeuresDeJeu = null,
    // Le mode de boost secteur du plan actif et le défaut de sa personnalité. null = non lu.
    ModeDeBoostLu? ModeDeBoost = null,
    // « Améliorer la précision du pointeur » (HKCU Control Panel\Mouse, MouseSpeed). On = accélération
    // active, Off = coupée, Unknown = valeur absente ou d'un autre type, null = non lu (lecteur non branché).
    TriState? AccelerationSouris = null,
    // La limite de puissance du processeur Intel lue par PawnIO (PL1, PL2, verrou, copie MMIO, maximum
    // publié). null = non lue (PawnIO absent, pas Intel, sans droits) : le levier ne s'arme pas, le docteur le dit.
    LimiteDePuissanceLue? LimitePuissance = null,
    // Le mode d'économie d'énergie de la carte sans fil (valeur secteur du plan actif). null = non lu.
    EconomieSansFilLue? EconomieSansFil = null,
    // Le fichier d'échange géré par le système sur un disque dur, Windows sur un SSD (FichierDEchangeADeplacer) ; Unknown = non lu.
    TriState FichierDEchangeADeplacer = TriState.Unknown);

/// <summary>« Mode d'économie d'énergie » des « Paramètres de la carte sans fil » d'un plan, sur
/// secteur. Microsoft Learn (Wi-Fi power management) : l'économie « réduit la consommation au prix d'une latence de
/// transfert accrue ». 0 = performances maximales, 1 = économie faible, 2 = moyenne, 3 = maximale.</summary>
public sealed record EconomieSansFilLue(Guid Plan, uint Valeur)
{
    public static readonly Guid SousGroupe = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    public static readonly Guid Reglage = new("12bbebe6-58d6-4636-95bb-3217ef867c1a");
    public const uint PerformancesMaximales = 0;
    public const uint MaxDocumente = 3;
}

/// <summary>PERFBOOSTMODE sur secteur, lu dans un plan, avec le défaut de la personnalité de ce plan
/// (Microsoft Learn, « PERFBOOSTMODE » : 0 = désactivé quel que soit le mode du processeur, 1 à 4 = activé).</summary>
public sealed record ModeDeBoostLu(Guid Plan, uint Valeur, uint Defaut)
{
    /// <summary>GUID du réglage (Learn) ; sous-groupe GUID_PROCESSOR_SETTINGS_SUBGROUP.</summary>
    public static readonly Guid Reglage = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// <summary>La plus haute valeur que Microsoft documente (0 à 4) ; powercfg en énumère davantage, jamais écrites.</summary>
    public const uint MaxDocumente = 4;
}

/// <summary>
/// Le mode d'affichage actif compare aux modes du panneau a la meme resolution. <see cref="HasHeadroom"/> vaut Unknown
/// quand on ne peut pas trancher : une liste vide veut dire « pas pu enumerer », jamais « rien de mieux ». Dans Core
/// parce que <see cref="SettingsState"/> le porte.
/// </summary>
public readonly record struct RefreshHeadroom(TriState HasHeadroom, int CurrentHz, int BestHz);

/// <summary>Une carte reseau physique connectee et ses reglages NDIS. Chaque reglage est un
/// TriState : Unknown = la carte n'expose pas ce mot-cle, une vraie reponse, jamais devinee
/// comme Off.</summary>
public sealed record NetworkAdapterState(
    string Name,
    string Description,
    bool IsWired,
    TriState InterruptModeration,
    TriState Rss,
    TriState EnergyEfficientEthernet,
    // Les autres economies d'energie du lien, hors 802.3az : `EnableGreenEthernet` et `PowerSavingMode` chez Realtek,
    // actives sur la machine de reference. Unknown = la carte n'expose pas ces mots-cles, pas « economies coupees ».
    // Optionnel, en queue.
    TriState LinkPowerSaving = TriState.Unknown);

public interface IHardwareDetector
{
    Task<Result<HardwareInventory>> DetectAsync();
}

public interface ISettingsStateReader
{
    Task<Result<SettingsState>> ReadAsync(HardwareInventory hw);
}

// --- Stockage (pour l'UI, non noté, hors de HardwareInventory) ---

public record StorageDevice(string Model, string MediaKind /* "NVMe"|"SSD"|"HDD"|"Unknown" */, long SizeBytes);

public interface IStorageDetector
{
    Result<IReadOnlyList<StorageDevice>> Detect();
}
