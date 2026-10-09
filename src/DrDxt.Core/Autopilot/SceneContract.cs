namespace DrDxt.Core;

/// <summary>Ce que la scene mesuree represente reellement du jeu.</summary>
public enum SceneRepresentativity
{
    /// <summary>Personne n'a dit ce qui etait joue. Par defaut, et volontairement inconfortable :
    /// un profil construit sur une scene inconnue ne peut pas etre garde durablement.</summary>
    Unknown,

    /// <summary>Une vraie partie. La seule famille sur laquelle un profil se defend.</summary>
    Gameplay,

    /// <summary>Terrain d'entrainement, salle privee, mode tir. Reproductible et pauvre : elle
    /// permet de DECOUVRIR un candidat, pas de conclure sur le jeu.</summary>
    Entrainement,

    /// <summary>Menu, lobby, ecran de fin. Le PC n'y fait pas le travail d'une partie, et un
    /// gain trouve la ne dit rien de ce que le joueur ressentira.</summary>
    MenuLobby,

    /// <summary>aucun jeu ne tournait : machine au repos, bureau, premier scan.
    ///
    /// 23/09/2026. A ne jamais confondre avec <see cref="Unknown"/> : celui-la est une ignorance,
    /// celui-ci est un fait que la personne connait. Un premier scan se fait presque toujours ici,
    /// et jusqu'a ce jour le produit n'avait aucune case pour le dire — il fallait s'accuser de ne
    /// pas savoir. La consequence est la meme (aucun profil ne se defend au repos), la PHRASE ne
    /// l'est pas : on ne reclame pas a quelqu'un de declarer ce qu'il vient de declarer.</summary>
    HorsJeu,
}

/// <summary>A quoi sert cette session dans la vie d'un profil.</summary>
public enum SceneRole
{
    /// <summary>DECOUVERTE. On cherche un candidat, donc on prefere une scene stable — quitte a
    /// ce qu'elle ne represente qu'un coin de la carte.</summary>
    Decouverte,

    /// <summary>VALIDATION. On rejoue le profil deja trouve sur une scene plus large, pour
    /// verifier qu'on n'a pas optimise un coin calme.</summary>
    Validation,
}

/// <summary>
/// Ou la mesure a ete prise — et ce que ca autorise a en conclure.
///
/// La session Warzone du 07/09/2026 a ete jouee en « partie reelle, zone calme ». C'est une
/// vraie scene de gameplay, bien meilleure qu'un menu — et le profil ne le portait nulle part.
/// C'etait dit a l'oral, le code n'en savait rien, et rien n'empechait la session suivante
/// d'etre prise dans un lobby et traitee exactement pareil.
///
/// ══ decouverte puis validation ══
/// Une zone calme est le bon endroit pour TROUVER un candidat : elle bruite peu, donc elle resout
/// mieux. C'est le mauvais endroit pour conclure que le profil vaut pour tout Warzone. Le modele
/// separe donc les deux roles — un candidat se decouvre ou l'on voit clair, et se valide ou l'on
/// joue vraiment.
///
/// **Ce lot pose le modele, il ne l'industrialise pas.** `KeepDurable` dit aujourd'hui si un
/// keep est defendable a long terme ; il ne va pas encore chercher une seconde scene tout seul.
/// La distinction est ecrite pour que la suite ne soit pas a reconstruire, et pour qu'un profil
/// cesse d'etre muet sur l'endroit ou il est ne.
/// </summary>
/// <param name="Representativite">Ce que la scene represente. Declare par le joueur ou par
/// l'appelant — DrDXT ne sait pas reconnaitre un lobby, et pretendre le contraire serait pire
/// que de demander.</param>
/// <param name="Role">Decouverte ou validation.</param>
/// <param name="Description">Ce que le joueur en a dit, mot pour mot. « partie reelle, zone
/// calme » vaut mieux que n'importe quelle categorie.</param>
public sealed record SceneContract(
    SceneRepresentativity Representativite,
    SceneRole Role = SceneRole.Decouverte,
    string? Description = null)
{
    /// <summary>Le defaut : on ne sait pas, et le produit le dit.</summary>
    public static readonly SceneContract Inconnue = new(SceneRepresentativity.Unknown);

    /// <summary>Un KEEP trouve ici est-il defendable DURABLEMENT ?
    ///
    /// Non sur un menu ou un lobby : le PC n'y fait pas le travail d'une partie.
    /// Non sur une scene inconnue : on ne garde pas un reglage sur la foi d'un endroit dont on
    /// ignore tout.
    /// Non sur un entrainement seul : il decouvre, il ne conclut pas.
    /// Non sur une decouverte de gameplay tant qu'aucune validation n'a suivi — c'est exactement
    /// le piege du coin calme.</summary>
    public bool KeepDurable =>
        Representativite == SceneRepresentativity.Gameplay && Role == SceneRole.Validation;

    /// <summary>Ce qu'il manque pour rendre un keep durable, en une phrase — ou <c>null</c> quand
    /// il ne manque rien.</summary>
    public string? CeQuiManque => Representativite switch
    {
        SceneRepresentativity.MenuLobby =>
            "mesure dans un menu ou un lobby : le PC n'y fait pas le travail d'une partie",
        SceneRepresentativity.Unknown =>
            "personne n'a dit ce qui était joué : un profil ne se défend pas sur une scène inconnue",
        SceneRepresentativity.Entrainement =>
            "terrain d'entraînement : bon pour découvrir un candidat, pas pour conclure sur le jeu",
        SceneRepresentativity.HorsJeu =>
            "aucun jeu ne tournait pendant la mesure : la machine était au repos, ce qui se lit ici "
            + "est sa configuration, pas ce que tu ressens en partie",
        _ => Role == SceneRole.Validation
            ? null
            : "trouvé en scène de découverte : à revalider sur une phase de jeu plus large avant "
              + "d'être gardé durablement",
    };
}
