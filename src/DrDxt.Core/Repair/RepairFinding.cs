namespace DrDxt.Core;

/// <summary>
/// Ce qu'on sait d'un etat de la machine, et surtout ce qu'on n'en sait pas : non-default n'est pas un degat. Machine de
/// reference, relevé le 08/09/2026 : plan Equilibre, aucun coeur parque, `LowLatencyMode` absent, 1728x1080 sur un ecran
/// 1920x1080 ; quatre etats non-default, aucun defaut.
/// </summary>
public enum RepairStatus
{
    /// <summary>Lu, sans jugement : on a regarde, on ne conclut pas.</summary>
    Observed,

    /// <summary>C'est la valeur que Windows ou le constructeur pose. Rien a signaler.</summary>
    ExpectedDefault,

    /// <summary>Different du default, comme une intention (resolution choisie, plan change a la main) : dit, pas
    /// touche.</summary>
    UserChoiceProbable,

    /// <summary>Different du default, sans qu'on sache si c'est voulu : un ecart, pas une accusation.</summary>
    NonDefault,

    /// <summary>La signature d'un optimiseur, ou une valeur qu'on ne pose pas a la main : assez pour en parler, pas pour
    /// ecrire.</summary>
    Suspicious,

    /// <summary>Cet etat nuit (performance, stabilite, securite) : le seul statut qui autorise une reparation
    /// automatique.</summary>
    KnownHarmful,

    /// <summary>Lu, sans savoir l'interpreter : pas « sain », un trou qui doit se voir.</summary>
    Unknown,
}

/// <summary>Ce que DrDXT propose de faire de ce constat.</summary>
public enum RepairAction
{
    /// <summary>Rien. Toujours permis, et c'est le defaut.</summary>
    Aucune,

    /// <summary>Une recommandation, appliquee seulement si quelqu'un le demande.</summary>
    Recommandation,

    /// <summary>DrDXT peut le reparer seul. Reserve a <see cref="RepairStatus.KnownHarmful"/>.</summary>
    ReparationAutomatique,

    /// <summary>La correction passe par le BIOS : DrDXT diagnostique et guide, il n'ecrit jamais.</summary>
    GuidedBios,
}

/// <summary>
/// Un etat de la machine, et ce qu'il autorise. Seul un etat connu comme nuisible autorise une reparation automatique ;
/// un etat inconnu ne se repare jamais seul. La regle est dans le constructeur : un oubli d'appelant ecrirait sur la
/// machine de quelqu'un.
/// </summary>
/// <param name="Domaine">power, BCD, RAM, GPU driver, display… : le regroupement de l'audit.</param>
/// <param name="Nom">L'etat, nomme comme le joueur peut le retrouver.</param>
/// <param name="ValeurCourante">Ce qui est lu. <c>null</c> = pas lisible, jamais une valeur
/// fabriquee.</param>
/// <param name="ValeurAttendue">Le default ou le known-good, quand il est defendable ; <c>null</c> sans reference
/// honnete, frequent et normal.</param>
/// <param name="Statut">Ce qu'on sait de cet ecart.</param>
/// <param name="Explication">Pourquoi ce constat existe, en clair. Obligatoire.</param>
/// <param name="Action">Ce que DrDXT propose. Contraint par le statut.</param>
/// <param name="Provenance">Ce qui a probablement pose cette valeur, seulement si c'est reellement identifiable : un
/// coupable non identifie donnerait une certitude fausse. <c>null</c> est la valeur normale.</param>
/// <param name="Remede">Comment corriger, en gestes (où cliquer, quoi mettre), à part de l'explication pour se lire sans
/// chercher. <c>null</c> = le geste, s'il existe, est encore dans l'explication.</param>
/// <param name="Cible">Ce que le geste de DrDXT viserait, tel que lu sur la machine (l'identifiant d'un périphérique,
/// les paquets oemNN.inf d'un magasin de pilotes). <c>null</c> = aucun geste ne s'y rattache.</param>
public sealed record RepairFinding(
    string Domaine,
    string Nom,
    string? ValeurCourante,
    string? ValeurAttendue,
    RepairStatus Statut,
    string Explication,
    RepairAction Action = RepairAction.Aucune,
    string? Provenance = null,
    string? Remede = null,
    string? Cible = null)
{
    /// <summary>Les statuts qui autorisent DrDXT a ecrire tout seul : un seul, exprès.</summary>
    public static readonly System.Collections.Frozen.FrozenSet<RepairStatus> AutorisentUneReparationAutomatique =
        System.Collections.Frozen.FrozenSet.ToFrozenSet([RepairStatus.KnownHarmful]);

    public bool NecessiteUneDecisionHumaine =>
        Action is RepairAction.Recommandation or RepairAction.GuidedBios;

    // La validation vit dans les initialiseurs du constructeur primaire ET dans les accesseurs init : un `with` copie le
    // record puis passe par les accesseurs, jamais par les initialiseurs. Un `with` qui change statut et action pose
    // d'abord celui qui garde le constat légal.
    private readonly string _explication = ExplicationValide(Explication);
    public string Explication { get => _explication; init => _explication = ExplicationValide(value); }

    private readonly RepairAction _action = ActionValide(Action, Statut);
    public RepairAction Action { get => _action; init => _action = ActionValide(value, _statut); }

    private readonly RepairStatus _statut = Statut;
    public RepairStatus Statut { get => _statut; init { ActionValide(_action, value); _statut = value; } }

    private static string ExplicationValide(string explication) => string.IsNullOrWhiteSpace(explication)
        ? throw new ArgumentException(
            "a finding without an explanation is just authority: fill in the explanation",
            nameof(Explication))
        : explication;

    private static RepairAction ActionValide(RepairAction action, RepairStatus statut) =>
        action == RepairAction.ReparationAutomatique && !AutorisentUneReparationAutomatique.Contains(statut)
            ? throw new ArgumentException(
                $"status {statut}: an automatic repair requires {nameof(RepairStatus.KnownHarmful)}. "
                + "\"Different\" is not \"damaged\", and Unknown never repairs itself.",
                nameof(Action))
            : action;
}
