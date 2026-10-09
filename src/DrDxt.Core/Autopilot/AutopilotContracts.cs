namespace DrDxt.Core;

/// <summary>
/// La nature d'une action — parce que « tweak » ne dit pas assez pour decider quoi en faire.
///
/// Un moteur qui ne connait qu'un `ITweak` indistinct ne peut ni choisir l'ordre des essais, ni
/// savoir ce qu'il a le droit de tenter tout seul. Ces cinq natures se traitent differemment, et
/// c'est le minimum pour qu'un autopilote soit honnete sur ce qu'il fait.
/// </summary>
public enum CandidateKind
{
    /// <summary>Reversible immediatement, donc **testable en a-b-a-b**. C'est la seule nature que
    /// l'autopilote applique et mesure de lui-meme.</summary>
    AutoHot,

    /// <summary>Applicable seul, mais l'effet n'existe qu'apres un redemarrage : impossible a
    /// mesurer dans la session. Un A/B lance dessus mesurerait le neant.</summary>
    AutoReboot,

    /// <summary>06/10/2026 (L-WU) : applicable seul et tout de suite, mais sans effet sur les images (les heures
    /// d'activité de Windows Update) : un A/B en jeu mesurerait le néant. Posé au clic comme <see cref="AutoReboot"/>, il
    /// se juge ailleurs (les redémarrages tombés pendant une partie).</summary>
    AutoReglage,

    /// <summary>XMP/EXPO, ReBAR, et tout ce que DrDXT n'ecrit pas lui-meme. Le produit peut le
    /// RECOMMANDER et guider, jamais l'appliquer.</summary>
    GuidedBios,

    /// <summary>Un pilote a reinstaller, une action lourde exterieure au produit.</summary>
    ExternalRepair,

    /// <summary>Aucune ecriture. Sert a expliquer, pas a corriger.</summary>
    DiagnosticOnly,
}

/// <summary>La famille d'un levier. Le root-cause classe des FAMILLES avant de choisir un
/// identifiant : « le GPU n'est pas sature et un thread de jeu est sous pression » designe
/// CPU/Scheduler, quel que soit le catalogue disponible ce jour-la.</summary>
public enum CandidateFamily
{
    /// <summary>Inconnue — le defaut, et il ne se classe jamais en tete.</summary>
    Inconnue,
    Repair,
    Cpu,
    Gpu,
    Memory,
    Scheduler,
    Latency,
    Network,
    Thermal,
}

/// <summary>
/// Ce qu'un levier ecrit vraiment, independamment de sa famille d'affichage.
///
/// 11/09/2026 : le veto des arrets brutaux (L6) fermait sur les grandeurs DECLAREES (`gpu.`,
/// `cpu.`, `ram.`). `latency.reflex`, `latency.render_queue_cap` et `latency.vrr_vsync_cap`
/// ecrivent l'etat du pilote NVIDIA (cle NVTweak, reglages DRS) et se declarent « latence » : ils
/// passaient, et l'Optimiser de l'App tentait `latency.reflex` sur une machine a sept arrets secs.
/// La surface se decide en lisant les operations privilegiees du levier, pas son nom.
/// </summary>
public enum SurfaceTouchee
{
    /// <summary>Le defaut : un levier qui ne dit pas ce qu'il ecrit est traite comme le pire.</summary>
    NonDeclaree = 0,

    /// <summary>Preferences de la session utilisateur (HKCU) : ni materiel, ni pilote.</summary>
    OsUtilisateur,

    /// <summary>Politique du processeur (plan d'alimentation, CPPC, service de routage X3D).</summary>
    Processeur,

    /// <summary>Configuration de la memoire (fichier d'echange).</summary>
    Memoire,

    /// <summary>La carte elle-meme (frequences, tension, puissance, ventilateurs).</summary>
    CarteGraphique,

    /// <summary>L'etat du pilote graphique et de son ordonnancement (DRS, NVTweak, Anti-Lag, HAGS).</summary>
    PiloteGraphique,

    /// <summary>N'ecrit rien lui-meme (un guide).</summary>
    Aucune,

    /// <summary>06/10/2026 (L-WU) : quand Windows Update s'autorise a redemarrer (heures d'activite). Ni materiel, ni
    /// pilote : un arret sec ne peut pas lui etre attribue.</summary>
    PlanificationWindows,

    /// <summary>Le mode d'affichage d'un ecran (ChangeDisplaySettingsEx, profil de l'utilisateur). Le pilote graphique
    /// pose le mode : des arrets brutaux le ferment comme le reste.</summary>
    Affichage,
}

/// <summary>Ce qu'une action declare d'elle-meme, avant qu'on la tente.</summary>
/// <param name="Id">Identifiant stable, ex. <c>gpu.core_offset</c>.</param>
/// <param name="Kind">Ce que l'autopilote a le droit d'en faire.</param>
/// <param name="RiskLevel">0 = inoffensif, 3 = peut desstabiliser la machine. Sert a ordonner les
/// essais : a gain potentiel egal, on tente le moins risque d'abord.</param>
/// <param name="NeedsStability">Faut-il verifier la stabilite apres application ? Un overclock
/// oui, un plan d'alimentation non.</param>
/// <param name="AffectedMetrics">Ce que cette action peut **theoriquement** changer. Sert a ne pas
/// la tenter quand le goulot est ailleurs — et a ne jamais lui attribuer un effet hors de son
/// domaine.</param>
public sealed record CandidateDescriptor(
    string Id,
    CandidateKind Kind,
    int RiskLevel,
    bool NeedsStability,
    IReadOnlyList<string> AffectedMetrics,

    /// <summary>Ce que l'APPELANT a constate sur la machine et qui empeche ce levier de
    /// s'appliquer — <c>null</c> quand rien ne l'empeche.
    ///
    /// Ajoute le 08/09/2026 apres que le probe de selection a annonce « 2 candidats retenus »
    /// alors que les deux etaient inapplicables. <c>latency.reflex</c> n'a pas de valeur d'origine
    /// a restaurer sur cette machine — la cle <c>NVTweak\LowLatencyMode</c> n'existe pas, donc le
    /// tweak refuse d'ecrire plutot que d'ecrire sans pouvoir revenir en arriere. Et
    /// <c>fixit.power_plan</c> exige le symptome de parking de coeurs, lu comme absent.
    ///
    /// Le selecteur ne peut pas deviner ces conditions : elles vivent dans le <c>AppliesTo</c> de
    /// chaque tweak et dans l'etat lu de la machine. L'appelant les lit, le selecteur les compte.</summary>
    string? BlocageConstate = null,

    /// <summary>Sa famille. `Inconnue` par defaut : un levier qui ne declare pas sa famille ne
    /// doit pas se retrouver en tete d'un classement par famille.</summary>
    CandidateFamily Family = CandidateFamily.Inconnue,

    /// <summary>PHASE 18 F5 — l'effet attendu, en pourcent de la grandeur qui decide, SOURCE (jamais
    /// devine). <c>null</c> = inconnu : le moteur se comporte comme avant. Connu, il decide avant
    /// toute ecriture si la scene peut le voir, et fixe d'avance le nombre d'alternances
    /// (<see cref="Decidabilite"/>).</summary>
    double? EffetAttenduPct = null,

    /// <summary>Ce que le levier ecrit (voir <see cref="SurfaceTouchee"/>). `NonDeclaree` par
    /// defaut, et le veto des arrets brutaux la ferme : l'oubli se paie en refus, jamais en ecriture.</summary>
    SurfaceTouchee Surface = SurfaceTouchee.NonDeclaree);

/// <summary>
/// Une action que l'autopilote peut reellement tenter.
///
/// <see cref="ReadBack"/> n'est pas une formalite. Mesure du 01/09/2026 : `tools/r2apply`
/// affichait « power limit applied: 110% » puis relisait **100 %** — la carte n'a pas la marge, le
/// pilote clampe en silence, et l'ecriture rend Ok. Une action qui ne relit pas ce qu'elle a ecrit
/// peut faire mesurer un etat qui n'existe pas.
/// </summary>
public interface IAutopilotCandidate
{
    CandidateDescriptor Descriptor { get; }

    /// <summary>Applique. Doit echouer proprement plutot que d'appliquer a moitie.</summary>
    Result Apply();

    /// <summary>Remet l'etat de reference. C'est le chemin de secours : il doit marcher meme
    /// apres un Apply rate.</summary>
    Result Revert();

    /// <summary>Relit l'etat reel apres application. <c>false</c> = l'ecriture n'a pas pris, et
    /// mesurer serait mesurer autre chose que ce qu'on croit.</summary>
    Result<bool> ReadBack();

    /// <summary>Le moteur a garde ce levier, et l'a relu en place : il reste pose au-dela de la
    /// session. Appele une fois, apres la decision et la relecture, jamais avant.
    ///
    /// 11/09/2026 : sans ce signal, le filet d'une session tuee (`pending-revert.json`) restait
    /// arme apres un keep, et le demarrage suivant rendait le levier garde en annoncant « la derniere
    /// session a ete interrompue » — le produit defaisait exactement le gain qu'il venait de prouver.
    /// Un echec ici laisse le levier pose avec son filet arme : il sera rendu au prochain demarrage,
    /// le defaut sur. Par defaut, rien a faire (un candidat sans journal).</summary>
    Result Garder() => Result.Ok();
}
