namespace DrDxt.Core;

// --- P0 Detection ---

public enum ChassisType { Desktop, Laptop, Handheld, Unknown }

public enum TriState { On, Off, Unknown }

public record CpuInfo(string Model, string Vendor, int Cores, int Threads, bool IsX3D, int CcdCount);

public record GpuInfo(string Model, string Vendor, long VramBytes, string DriverVersion, TriState ReBar);

// Les quatre derniers champs sont OPTIONNELS/en queue : toutes les constructions existantes
// restent valides. 0 / null = « jamais lu », jamais « zero volt » ni « zero rang » -- la
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
    // Prouve qu'un profil XMP/EXPO est charge, independamment de la frequence. 0 = non lu.
    int ConfiguredVoltageMv = 0,
    // Emplacement physique (« Controller0-DIMMA2 »). C'est une chaine de TOPOLOGIE : elle dit ou
    // la barrette est branchee, pas le mode du controleur. Corrige le 08/09/2026 : deux controleurs
    // distincts ne PROUVENT pas le double canal (seul un debit au-dessus du plafond d'un canal le
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
    // 06/10/2026 (chantier B1) : les emplacements mémoire de la carte (SMBIOS type 16). 0 = non lu.
    int MemorySlots = 0);

public enum RestartVerdict
{
    /// <summary>Nothing is waiting on a restart; a change under test, if given, is in effect.</summary>
    NoRestartPending,

    /// <summary>A restart is owed. Any measurement taken now describes the wrong configuration.</summary>
    RestartRequired,

    /// <summary>Cannot conclude. <see cref="RestartState.Reason"/> says what is missing or in the way.</summary>
    Indeterminate,
}

/// <param name="Reason">Plain-language why, safe to show a player as-is.</param>
public readonly record struct RestartState(RestartVerdict Verdict, string Reason);

public enum DrsValueOrigin
{
    /// <summary>Set on this profile and not a driver-shipped value: a human changed it.</summary>
    SetByUser,

    /// <summary>Set on this profile, but shipped by NVIDIA with the driver — a predefined value,
    /// not a choice anyone made.</summary>
    ShippedWithTheDriver,

    /// <summary>Reported from the global profile: this profile owns nothing here.</summary>
    InheritedFromGlobalProfile,

    /// <summary>Reported from the base profile: this profile owns nothing here.</summary>
    InheritedFromBaseProfile,

    /// <summary>The driver default, set nowhere.</summary>
    DriverDefault,

    /// <summary>A location the driver reported that we do not recognise. Never collapsed into a
    /// confident answer.</summary>
    Unknown,
}

/// <param name="IsYourOwnChange">True only for <see cref="DrsValueOrigin.SetByUser"/> — the single
/// case where pointing a person at this profile makes sense.</param>
/// <param name="Reason">Plain-language why, safe to show as-is.</param>
public readonly record struct DrsOriginState(DrsValueOrigin Origin, bool IsYourOwnChange, string Reason);

public enum FrameCapVerdict
{
    /// <summary>The cap is the low-latency value for the display the game runs on (refresh - 3).</summary>
    Coherent,

    /// <summary>The cap is the low-latency value of an attached but non-primary display. It was
    /// computed on the wrong panel and costs frames on the one being played on.</summary>
    ComputedOnAnotherDisplay,

    /// <summary>The cap binds under the primary refresh but matches no attached display's rule.
    /// Reported without naming a cause we did not establish.</summary>
    CapsBelowPrimary,

    /// <summary>0 (driver "off") or a value at/above the primary refresh: it never binds.</summary>
    NotCapping,

    /// <summary>Not enough was read to conclude. Never a clean bill of health.</summary>
    Indeterminate,
}

/// <param name="Reason">Plain-language why, safe to show a player as-is.</param>
/// <param name="Origin">Who put the cap there, when the driver was asked. null = not read —
/// never "nobody". It INFORMS the reader; it never changes the verdict, because where a cap
/// came from does not change whether it costs frames.</param>
public readonly record struct FrameCapState(
    FrameCapVerdict Verdict, string Reason, DrsOriginState? Origin = null);

public record SettingsState(
    TriState XmpExpo,
    TriState ReBar,
    string PowerPlanGuid,
    TriState Hags,
    // TriState et non bool : un bool ne sait pas dire « illisible ». Le lecteur ecrasait
    // TriState.Unknown en false, donc sur une machine ou le reglage ne se lit pas, DrDXT
    // concluait « Game Mode desactive » et proposait de le reparer — un deficit FABRIQUE a
    // partir d'une lecture ratee, l'exact contraire de la regle « Unknown ne deduit RIEN ».
    TriState GameMode,
    // null = le parking n'a pas PU etre lu. Une liste vide veut dire "lu, et aucun coeur
    // n'est parque" : ce sont deux etats differents, et les confondre a fait proposer une
    // Ecriture sur des machines saines pendant que le scan les declarait sans probleme.
    IReadOnlyList<int>? ParkedCoreIndexes,
    TriState Vrr,
    // H2 gating hook: VRR off is a deficit only when a VRR-capable display is confirmed
    // (TriState.On). Until display-capability detection is wired, this stays Unknown, so VRR off
    // is treated as a neutral opportunity (0 penalty), never an unjust deduction. Optional/trailing
    // so existing 7-arg constructions stay source-compatible.
    TriState VrrCapableDisplay = TriState.Unknown,
    // NS-4 (RAM reliability): page file present (On) / DISABLED (Off) / unreadable (Unknown).
    // Off is a reliability deficit (OOM/freeze), not a "+FPS" opportunity. Optional/trailing.
    TriState PagefileConfigured = TriState.Unknown,
    // NS-9 (reseau) : cartes physiques CONNECTEES + leurs mots-cles NDIS. null = jamais lu
    // (ancienne construction a 9 arguments) ; liste vide = lu, aucune carte connectee. La
    // distinction compte : « pas lu » n'est pas « pas de reseau ». Optional/trailing.
    IReadOnlyList<NetworkAdapterState>? NetworkAdapters = null,
    // M3 lot 1 — la GRANDEUR DURE derriere XmpExpo. XmpExpo est une INFERENCE (XmpInferencer
    // s'etiquette lui-meme « probably ») : sur un kit dont le socle JEDEC est depasse dans les
    // deux etats, elle ne bouge pas alors que la machine, si. La boucle de verification a besoin
    // du chiffre, pas du pari : 2133 -> 3800 est un fait. 0 = jamais lu (jamais « 0 MHz »).
    int MemoryConfiguredMhz = 0,
    // M3 lot 1 — CSM. On ne peut pas le lire directement, mais un demarrage LEGACY le prouve
    // actif. Un demarrage UEFI ne prouve pas l'inverse (le CSM peut etre actif sous UEFI) =>
    // Legacy => On, UEFI => Unknown. On ne fabrique pas un Off qu'on n'a pas mesure.
    TriState Csm = TriState.Unknown,
    // Restart state (M3): a measurement taken while Windows owes a restart describes the WRONG
    // configuration. null = never computed (older constructions stay source-compatible).
    RestartState? Restart = null,
    // Frame cap (M3): a cap computed on the wrong display costs frames on every session
    // and was read for months as a revert value without a word. null = never computed.
    FrameCapState? FrameCap = null,
    // L1.4 — l'ecran tourne-t-il sous sa frequence maximale ? Le levier le plus gratuit d'un PC
    // de jeu, et celui qu'aucun autre ne compense : un panneau 200 Hz pilote a 143 Hz jette
    // 28 % des images que le GPU rend deja. L'heuristique existait et etait prouvee depuis le
    // 15/08 ; elle n'etait CABLEE nulle part, et un scan declarait la machine saine.
    // null = jamais calcule (aucune sonde d'affichage), jamais « rien a signaler ».
    RefreshHeadroom? DisplayRefresh = null,
    // L1.5 — les degats deja infliges. « Ce qui coute des images » dans sa forme la plus
    // frequente n'est pas un reglage jamais active : c'est un defaut Windows qu'un
    // « optimiseur » precedent a casse. Prevus depuis le modele de notation initial (non publie), jamais
    // implementes faute de lecteurs. Unknown = pas lu, jamais « tout va bien ».
    /// <summary>SysMain encore actif, ou desactive par quelqu'un.</summary>
    TriState SysMain = TriState.Unknown,
    /// <summary>MMCSS intact, ou `SystemResponsiveness` mis a 0 (le mythe le plus recopie).</summary>
    TriState MmcssIntact = TriState.Unknown,
    // LR — les redemarrages sans arret propre (`Kernel-Power 41`), plus recents d'abord.
    // null = jamais LU ; liste vide = lu, et la machine n'a redemarre salement aucune fois.
    // La distinction decide de tout : « pas lu » n'est pas « tout va bien », et c'est la faute
    // exacte que le parking de coeurs a deja fait payer a ce depot.
    // Ce compte n'est pas un compte de plantages : une extinction au bouton ou a la prise se
    // logue a l'identique. Tout lecteur de ce champ doit porter cette reserve.
    IReadOnlyList<DateTimeOffset>? UncleanShutdowns = null,
    // LM — le nombre de canaux memoire que la mesure prouve. Un canal a un plafond de debit
    // (MT/s x 8 octets) et rien ne le depasse : lire au-dessus prouve qu'il y en a plusieurs,
    // meme quand la carte nomme mal ses emplacements et que `MemoryTopologyAdvisor` doit avouer
    // qu'il ne sait pas.
    // 0 = jamais mesure, 1 = mesure sans rien prouver. Ni l'un ni l'autre ne veut dire
    // « simple canal » : un seul fil d'execution ne sature pas forcement deux canaux, donc un
    // debit bas ne conclut rien. Seul un compte >= 2 ferme une inconnue.
    int MemoryProvenChannels = 0,
    // LM 07/09 — la frequence de chaque ecran attache. Le lecteur lisait deja tous les ecrans et
    // n'en gardait que le principal : la question « les frequences different-elles ? » etait donc
    // impossible a poser, alors que c'est elle qui decide de la consommation AU REPOS.
    // null = jamais lu. Un 0 dans la liste = une lecture ratee, jamais un ecran a 0 Hz.
    IReadOnlyList<int>? AttachedRefreshHz = null,
    // PHASE 18 — l'etat courant du mode de gestion d'alimentation du pilote NVIDIA
    // (`PREFERRED_PSTATE`). On = « Prefer maximum performance » deja actif, Off = « Adaptive »
    // (le defaut pilote), Unknown = non lu.
    //
    // Pourquoi ce champ existe. Le levier `latency.prefer_max_perf` ne se gardait que sur
    // « bureau + GPU NVIDIA » : il ne regardait pas si la valeur cible etait deja en place. Sur la
    // machine de reference, lecture du 10/09/2026 : **elle l'est deja** (`PREFERRED_PSTATE = 1`).
    // Le produit aurait donc propose un levier sans effet, l'aurait « applique » sans rien
    // changer, puis aurait mesure le bruit d'une session — le placebo exact que DrDXT existe pour
    // ne pas vendre.
    //
    // Unknown ne se corrige pas : sans lecture, pas de candidat.
    TriState NvidiaPreferMaxPerf = TriState.Unknown,
    // PHASE 18 F1 — D'OU vient cette valeur (null = non lue) et a quel scope elle a ete lue :
    // le chemin du jeu, ou null = profil global (jeu inconnu, ou jeu sans profil propre). Mesure
    // du 10/09 : sur cod.exe la valeur est HERITEE du global — le jeu ne la possede pas.
    DrsOriginState? NvidiaPreferMaxPerfOrigin = null,
    string? NvidiaDrsScope = null,
    // PHASE 18 F2 — HAGS tel que Windows l'APPLIQUE, lu par D3DKMT (KMTQAITYPE_WDDM_2_7_CAPS) sur
    // l'adaptateur qui affiche — pas par la presence de `HwSchMode`. Mesure du 10/09 : cle absente,
    // HAGS actif (0x0B). Structure « reserved for system use » : lecture de bonne foi, jamais
    // pour ecrire. Unknown = non lu, non supporte, ou adaptateurs d'affichage qui se contredisent.
    TriState HagsEffective = TriState.Unknown,
    // PHASE 18 F3 — le goulot mesure par le scan (`BottleneckClassifier`), pose par l'App avant la
    // selection des leviers. null = aucune mesure en jeu : aucun levier qui en depend ne s'arme.
    BottleneckState? GoulotMesure = null,
    // 24/09/2026 — les reglages LUS dans la NVRAM du BIOS (NvramBiosSettingsReader), recoupes
    // avec Windows. Quand ils sont la, XmpExpo/ReBar/Csm en viennent : ce n'est plus une
    // inference. null = rien de lu, les champs gardent leur inference.
    IReadOnlyList<BiosSettingReading>? BiosLu = null,
    // 06/10/2026 (chantier L-WU) : les heures d'activité de Windows Update (null = non lues) et les parties relevées
    // (null = relevé non branché ; vide = aucune partie). Le levier des heures d'activité ne s'arme que sur les deux lus.
    HeuresActivesLues? HeuresActives = null,
    IReadOnlyList<PlageDeJeu>? HeuresDeJeu = null,
    // 07/10/2026 (L-CPU) : le mode de boost secteur du plan actif et le défaut de sa personnalité. null = non lu.
    ModeDeBoostLu? ModeDeBoost = null,
    // MOUSE1 (09/10/2026) : « Améliorer la précision du pointeur » (HKCU Control Panel\Mouse, MouseSpeed). On = accélération
    // active, Off = coupée, Unknown = valeur absente ou d'un autre type, null = non lu (lecteur non branché).
    TriState? AccelerationSouris = null,
    // B1 (10/10/2026) : la limite de puissance du processeur Intel lue par PawnIO (PL1, PL2, verrou, copie MMIO, maximum
    // publié). null = non lue (PawnIO absent, pas Intel, sans droits) : le levier ne s'arme pas, le docteur le dit.
    LimiteDePuissanceLue? LimitePuissance = null,
    // B3 (10/10/2026) : le mode d'économie d'énergie de la carte sans fil (valeur secteur du plan actif). null = non lu.
    EconomieSansFilLue? EconomieSansFil = null);

/// <summary>07/10/2026 (L-CPU) : PERFBOOSTMODE sur secteur, lu dans un plan, avec le défaut de la personnalité de ce plan
/// (Microsoft Learn, « PERFBOOSTMODE » : 0 = désactivé quel que soit le mode du processeur, 1 à 4 = activé).</summary>
/// <summary>B3 (10/10/2026) : « Mode d'économie d'énergie » des « Paramètres de la carte sans fil » d'un plan, sur
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
/// permettent pas de trancher — une liste de modes vide veut dire « je n'ai pas pu enumerer »,
/// jamais « il n'y a rien de mieux ».
///
/// Vit ici, dans Core, parce que <see cref="SettingsState"/> le porte : le type ne pouvait pas
/// rester dans l'assembly de detection sans faire remonter une dependance a l'envers.
/// </summary>
public readonly record struct RefreshHeadroom(TriState HasHeadroom, int CurrentHz, int BestHz);

/// <summary>Une carte reseau physique CONNECTEE et ses reglages NDIS. Chaque reglage est un
/// TriState : Unknown = la carte n'expose pas ce mot-cle — une vraie reponse, jamais devinee
/// comme Off.</summary>
public sealed record NetworkAdapterState(
    string Name,
    string Description,
    bool IsWired,
    TriState InterruptModeration,
    TriState Rss,
    TriState EnergyEfficientEthernet,
    // LM 07/09 — les autres economies d'energie du lien, hors 802.3az : `EnableGreenEthernet` et
    // `PowerSavingMode` chez Realtek. L'audit du scanner les a trouvees ACTIVES sur la machine de
    // reference alors que le produit disait « rien a signaler » : il ne les lisait pas.
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

// --- Storage (UI-facing, not scored, not in HardwareInventory) ---

public record StorageDevice(string Model, string MediaKind /* "NVMe"|"SSD"|"HDD"|"Unknown" */, long SizeBytes);

public interface IStorageDetector
{
    Result<IReadOnlyList<StorageDevice>> Detect();
}
