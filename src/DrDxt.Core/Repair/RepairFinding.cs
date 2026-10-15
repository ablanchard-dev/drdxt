namespace DrDxt.Core;

/// <summary>
/// Ce qu'on sait d'un etat de la machine, et surtout ce qu'on n'en sait pas.
///
/// La tentation d'un outil de reparation est de traiter tout etat non-default comme un degat.
/// Exemple releve sur la machine de reference le 08/09/2026 : plan Equilibre, aucun coeur parque,
/// `LowLatencyMode` absent, 1728x1080 sur un ecran 1920x1080. Quatre etats non-default, **aucun
/// n'est un defaut.** Un produit qui « repare » ca casse le PC de quelqu'un en croyant l'aider.
/// </summary>
public enum RepairStatus
{
    /// <summary>Lu, sans jugement. L'etat par defaut d'un audit honnete : on a regarde, on ne
    /// conclut pas.</summary>
    Observed,

    /// <summary>C'est la valeur que Windows ou le constructeur pose. Rien a signaler.</summary>
    ExpectedDefault,

    /// <summary>Different du default, et l'ecart ressemble a une intention : une resolution
    /// choisie, un plan d'alimentation change a la main. On le dit, on n'y touche pas.</summary>
    UserChoiceProbable,

    /// <summary>Different du default, sans qu'on sache si c'est voulu. « Different » n'est pas
    /// « endommage » : ce statut decrit un ecart, il n'accuse pas.</summary>
    NonDefault,

    /// <summary>L'etat porte la signature d'une modification d'optimiseur, ou une valeur qu'un
    /// utilisateur ne pose pas a la main. Assez pour en parler, pas pour ecrire.</summary>
    Suspicious,

    /// <summary>On sait que cet etat nuit : a la performance, a la stabilite ou a la securite.
    /// **Le seul statut qui autorise une reparation automatique.**</summary>
    KnownHarmful,

    /// <summary>Lu, et on ne sait pas l'interpreter. Ce n'est pas « sain » : c'est un trou dans
    /// notre connaissance, et il doit se voir.</summary>
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

    /// <summary>La correction passe par le BIOS : DrDXT diagnostique, explique et guide, il
    /// n'ecrit jamais.</summary>
    GuidedBios,
}

/// <summary>
/// Un etat de la machine, et ce qu'il autorise.
///
/// **La regle que ce type rend impossible a violer** : une reparation automatique exige une
/// justification bien plus forte qu'un simple ecart au default. Seul un etat connu comme nuisible
/// l'autorise ; un etat inconnu ne se repare jamais tout seul.
///
/// Elle est gravee dans le constructeur plutot que verifiee par un appelant discipline, parce
/// qu'un oubli d'appelant, ici, ecrit sur la machine de quelqu'un.
/// </summary>
/// <param name="Domaine">power, BCD, RAM, GPU driver, display… : le regroupement de l'audit.</param>
/// <param name="Nom">L'etat, nomme comme le joueur peut le retrouver.</param>
/// <param name="ValeurCourante">Ce qui est lu. <c>null</c> = pas lisible, jamais une valeur
/// fabriquee.</param>
/// <param name="ValeurAttendue">Le default ou le known-good, quand il est defendable. <c>null</c>
/// quand nous n'avons pas de reference honnete, ce qui est frequent et normal.</param>
/// <param name="Statut">Ce qu'on sait de cet ecart.</param>
/// <param name="Explication">Pourquoi ce constat existe, en clair. Obligatoire : un constat sans
/// explication est une autorite.</param>
/// <param name="Action">Ce que DrDXT propose. Contraint par le statut.</param>
/// <param name="Provenance">Ce qui a probablement pose cette valeur, **uniquement si c'est
/// reellement identifiable**. Nommer un coupable qu'on n'a pas identifie donne au joueur une
/// certitude fausse et nous empeche de reconnaitre plus tard qu'on n'avait rien vu. <c>null</c>
/// est la valeur normale.</param>
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
    /// <summary>Les statuts qui autorisent DrDXT a ecrire tout seul. Un seul, et c'est
    /// deliberé.</summary>
    public static readonly RepairStatus[] AutorisentUneReparationAutomatique =
        { RepairStatus.KnownHarmful };

    public bool NecessiteUneDecisionHumaine =>
        Action is RepairAction.Recommandation or RepairAction.GuidedBios;

    // Simplification voulue : la validation vit dans les initialiseurs de propriete du constructeur primaire :
    // un appelant ne peut pas construire un constat illegal, meme en se trompant.
    public string Explication { get; init; } = string.IsNullOrWhiteSpace(Explication)
        ? throw new ArgumentException(
            "a finding without an explanation is just authority: fill in the explanation",
            nameof(Explication))
        : Explication;

    public RepairAction Action { get; init; } =
        Action == RepairAction.ReparationAutomatique
        && !AutorisentUneReparationAutomatique.Contains(Statut)
            ? throw new ArgumentException(
                $"status {Statut}: an automatic repair requires {nameof(RepairStatus.KnownHarmful)}. "
                + "\"Different\" is not \"damaged\", and Unknown never repairs itself.",
                nameof(Action))
            : Action;
}
