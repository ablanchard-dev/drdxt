namespace DrDxt.Core;

// --- P0 Détection ---

public enum ChassisType { Desktop, Laptop, Handheld, Unknown }

public enum TriState { On, Off, Unknown }

public record CpuInfo(string Model, string Vendor, int Cores, int Threads, bool IsX3D, int CcdCount);

public record GpuInfo(string Model, string Vendor, long VramBytes, string DriverVersion, TriState ReBar);

// Les quatre derniers champs sont optionnels/en queue : toutes les constructions existantes
// restent valides. 0 / null = « jamais lu », jamais « zero volt » ni « zero rang » : la
// distinction decide de tout ce qui juge derriere.
public record MemoryModule(
    string PartNumber,
    string Manufacturer,
    int ConfiguredMhz,
    int RatedMhz,
    // SMBIOS Memory Device Type. 26 = DDR4 (mesure sur la machine de reference), 34 = DDR5
    // (valeur de la specification, pas verifiee ici). 0 = non lu.
    int SmbiosMemoryType = 0,
    // Rangs par module (SMBIOS Type 17 Attributes, bits 3:0). Le dual rank change le plafond
    // de frequence atteignable : un conseil qui l'ignore est faux. 0 = non lu.
    int Ranks = 0,
    // Tension configuree en mV. DDR4 JEDEC = 1200, DDR5 = 1100 : une tension nettement au-dessus
    // prouve qu'un profil XMP/EXPO est charge, independamment de la frequence. 0 = non lu.
    int ConfiguredVoltageMv = 0,
    // Emplacement physique (« Controller0-DIMMA2 »). C'est une chaine de topologie : elle dit ou
    // la barrette est branchee, pas le mode du controleur. Deux controleurs
    // distincts ne prouvent pas le double canal (seul un debit au-dessus du plafond d'un canal le
    // prouve, voir MemoryInference). null = non lu.
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
/// <param name="Origin">Qui a posé le plafond, quand on a interrogé le pilote. null = non lu,
/// jamais « personne ». Il informe le lecteur ; il ne change jamais le verdict, car l'origine
/// d'un plafond ne change pas le fait qu'il coûte des images.</param>
public readonly record struct FrameCapState(
    FrameCapVerdict Verdict, string Reason, DrsOriginState? Origin = null);

public record SettingsState(
    TriState XmpExpo,
    TriState ReBar,
    string PowerPlanGuid,
    TriState Hags,
    // TriState et non bool : un bool ne sait pas dire « illisible ». Le lecteur ecrasait
    // TriState.Unknown en false, donc sur une machine ou le reglage ne se lit pas, DrDXT
    // concluait « Game Mode desactive » et proposait de le reparer : un deficit fabrique a
    // partir d'une lecture ratee, l'exact contraire de la regle « Unknown ne deduit rien ».
    TriState GameMode,
    // null = le parking n'a pas PU etre lu. Une liste vide veut dire "lu, et aucun coeur
    // n'est parque" : ce sont deux etats differents, et les confondre a fait proposer une
    // Ecriture sur des machines saines pendant que le scan les declarait sans probleme.
    IReadOnlyList<int>? ParkedCoreIndexes,
    TriState Vrr,
    // Condition : VRR coupé n'est un déficit que si un écran compatible VRR est confirmé
    // (TriState.On). Tant que la détection des capacités d'écran n'est pas branchée, ceci reste Unknown :
    // VRR coupé est une occasion neutre (0 pénalité), jamais une retenue injuste. Facultatif, en
    // dernier : les constructions à 7 arguments compilent toujours.
    TriState VrrCapableDisplay = TriState.Unknown,
    // Fiabilité de la RAM : fichier d'échange présent (On) / désactivé (Off) / illisible (Unknown).
    // Off est un déficit de fiabilité (OOM, gel), pas une occasion de « +FPS ». Facultatif, en dernier.
    TriState PagefileConfigured = TriState.Unknown,
    // Reseau : cartes physiques connectees + leurs mots-cles NDIS. null = jamais lu
    // (ancienne construction a 9 arguments) ; liste vide = lu, aucune carte connectee. La
    // distinction compte : « pas lu » n'est pas « pas de reseau ». Facultatif, en dernier.
    IReadOnlyList<NetworkAdapterState>? NetworkAdapters = null,
    // La grandeur dure derriere XmpExpo. XmpExpo est une inference (XmpInferencer
    // s'etiquette lui-meme « probably ») : sur un kit dont le socle JEDEC est depasse dans les
    // deux etats, elle ne bouge pas alors que la machine, si. La boucle de verification a besoin
    // du chiffre, pas du pari : 2133 -> 3800 est un fait. 0 = jamais lu (jamais « 0 MHz »).
    int MemoryConfiguredMhz = 0,
    // CSM. On ne peut pas le lire directement, mais un demarrage Legacy le prouve
    // actif. Un demarrage UEFI ne prouve pas l'inverse (le CSM peut etre actif sous UEFI) =>
    // Legacy => On, UEFI => Unknown. On ne fabrique pas un Off qu'on n'a pas mesure.
    TriState Csm = TriState.Unknown,
    // État de redémarrage : une mesure prise quand Windows doit redémarrer décrit la mauvaise
    // configuration. null = jamais calculé (les anciennes constructions compilent toujours).
    RestartState? Restart = null,
    // Plafond d'images : un plafond calculé sur le mauvais écran coûte des images à chaque session
    // et a été lu des mois comme valeur de retour sans un mot. null = jamais calculé.
    FrameCapState? FrameCap = null,
    // L'ecran tourne-t-il sous sa frequence maximale ? Le levier le plus gratuit d'un PC
    // de jeu, et celui qu'aucun autre ne compense : un panneau 200 Hz pilote a 143 Hz jette
    // 28 % des images que le GPU rend deja. L'heuristique existait et etait prouvee depuis le
    // 15/08 ; elle n'etait cablee nulle part, et un scan declarait la machine saine.
    // null = jamais calcule (aucune sonde d'affichage), jamais « rien a signaler ».
    RefreshHeadroom? DisplayRefresh = null,
    // Les degats deja infliges. « Ce qui coute des images » dans sa forme la plus
    // frequente n'est pas un reglage jamais active : c'est un defaut Windows qu'un
    // « optimiseur » precedent a casse. Prevus depuis le modele de notation initial (non publie), jamais
    // implementes faute de lecteurs. Unknown = pas lu, jamais « tout va bien ».
    /// <summary>SysMain encore actif, ou desactive par quelqu'un.</summary>
    TriState SysMain = TriState.Unknown,
    /// <summary>MMCSS intact, ou coupe par `SystemResponsiveness` a 100 (sous 10, Windows ramene a 20 : sans effet).</summary>
    TriState MmcssIntact = TriState.Unknown,
    // LR : les redemarrages sans arret propre (`Kernel-Power 41`), plus recents d'abord.
    // null = jamais lu ; liste vide = lu, et la machine n'a redemarre salement aucune fois.
    // La distinction decide de tout : « pas lu » n'est pas « tout va bien », et c'est la faute
    // exacte que le parking de coeurs a deja fait payer a ce depot.
    // Ce compte n'est pas un compte de plantages : une extinction au bouton ou a la prise se
    // logue a l'identique. Tout lecteur de ce champ doit porter cette reserve.
    IReadOnlyList<DateTimeOffset>? UncleanShutdowns = null,
    // Les incidents datés de la garde de stabilité (arrêts brutaux classés, écrans bleus, vidages, erreurs matérielles
    // graves, hausses d'arrêts dangereux du SSD) : la même source que la garde Curve Optimizer. null = une source non lue,
    // ou lecteur non branché ; une garde le lit comme « instable », jamais comme « stable ».
    IReadOnlyList<DateTimeOffset>? IncidentsDeStabilite = null,
    // Le nombre de canaux memoire que la mesure prouve. Un canal a un plafond de debit
    // (MT/s x 8 octets) et rien ne le depasse : lire au-dessus prouve qu'il y en a plusieurs,
    // meme quand la carte nomme mal ses emplacements et que `MemoryTopologyAdvisor` doit avouer
    // qu'il ne sait pas.
    // 0 = jamais mesure, 1 = mesure sans rien prouver. Ni l'un ni l'autre ne veut dire
    // « simple canal » : un seul fil d'execution ne sature pas forcement deux canaux, donc un
    // debit bas ne conclut rien. Seul un compte >= 2 ferme une inconnue.
    int MemoryProvenChannels = 0,
    // Un seul canal probable, dit par la mesure multi-fil (au moins deux barrettes, et plusieurs fils sous le plafond
    // d'un canal). La seule conclusion vers le bas que DrDXT tire du debit.
    bool MemoryUnSeulCanalMesure = false,
    // La frequence de chaque ecran attache. Le lecteur lisait deja tous les ecrans et
    // n'en gardait que le principal : la question « les frequences different-elles ? » etait donc
    // impossible a poser, alors que c'est elle qui decide de la consommation au repos.
    // null = jamais lu. Un 0 dans la liste = une lecture ratee, jamais un ecran a 0 Hz.
    IReadOnlyList<int>? AttachedRefreshHz = null,
    // L'etat courant du mode de gestion d'alimentation du pilote NVIDIA
    // (`PREFERRED_PSTATE`). On = « Prefer maximum performance » deja actif, Off = « Adaptive »
    // (le defaut pilote), Unknown = non lu.
    //
    // Le levier `latency.prefer_max_perf` doit savoir si la valeur cible est deja en place
    // (mesure le 10/09/2026 sur la machine de reference : `PREFERRED_PSTATE = 1`). Sinon il serait propose
    // sans effet, « applique » sans rien changer, puis jugerait le bruit d'une session : un placebo.
    //
    // Unknown ne se corrige pas : sans lecture, pas de candidat.
    TriState NvidiaPreferMaxPerf = TriState.Unknown,
    // D'ou vient cette valeur (null = non lue) et a quel scope elle a ete lue :
    // le chemin du jeu, ou null = profil global (jeu inconnu, ou jeu sans profil propre).
    // Mesure du 10/09 : sur cod.exe la valeur est heritee du global, le jeu ne la possede pas.
    DrsOriginState? NvidiaPreferMaxPerfOrigin = null,
    string? NvidiaDrsScope = null,
    // HAGS tel que Windows l'applique, lu par D3DKMT (KMTQAITYPE_WDDM_2_7_CAPS) sur
    // l'adaptateur qui affiche, pas par la presence de `HwSchMode`. Mesure du 10/09 : cle absente,
    // HAGS actif (0x0B). Structure « reserved for system use » : lecture de bonne foi, jamais
    // pour ecrire. Unknown = non lu, non supporte, ou adaptateurs d'affichage qui se contredisent.
    TriState HagsEffective = TriState.Unknown,
    // Le goulot mesure par le scan (`BottleneckClassifier`), pose par l'App avant la
    // selection des leviers. null = aucune mesure en jeu : aucun levier qui en depend ne s'arme.
    BottleneckState? GoulotMesure = null,
    // Les reglages lus dans la NVRAM du BIOS (NvramBiosSettingsReader), recoupes
    // avec Windows. Quand ils sont la, XmpExpo/ReBar/Csm en viennent : ce n'est plus une
    // inference. null = rien de lu, les champs gardent leur inference.
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

/// <summary>PERFBOOSTMODE sur secteur, lu dans un plan, avec le défaut de la personnalité de ce plan
/// (Microsoft Learn, « PERFBOOSTMODE » : 0 = désactivé quel que soit le mode du processeur, 1 à 4 = activé).</summary>
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

public sealed record ModeDeBoostLu(Guid Plan, uint Valeur, uint Defaut)
{
    /// <summary>GUID du réglage (Learn) ; sous-groupe GUID_PROCESSOR_SETTINGS_SUBGROUP.</summary>
    public static readonly Guid Reglage = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// <summary>La plus haute valeur que Microsoft documente (0 à 4) ; powercfg en énumère davantage, jamais écrites.</summary>
    public const uint MaxDocumente = 4;
}

/// <summary>
/// Le resultat de la comparaison entre le mode d'affichage actif et les modes que le panneau
/// offre a la meme resolution. <see cref="HasHeadroom"/> vaut Unknown quand les entrees ne
/// permettent pas de trancher : une liste de modes vide veut dire « je n'ai pas pu enumerer »,
/// jamais « il n'y a rien de mieux ».
///
/// Vit ici, dans Core, parce que <see cref="SettingsState"/> le porte : le type ne pouvait pas
/// rester dans l'assembly de detection sans faire remonter une dependance a l'envers.
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
    // Les autres economies d'energie du lien, hors 802.3az : `EnableGreenEthernet` et
    // `PowerSavingMode` chez Realtek. Relevees actives sur la machine de reference : sans ce champ,
    // le produit dirait « rien a signaler » sur ce qu'il ne lit pas.
    // Unknown = la carte n'expose pas ces mots-cles, ce qui n'est pas « economies coupees ».
    // Optionnel/en queue : toutes les constructions existantes restent valides.
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
