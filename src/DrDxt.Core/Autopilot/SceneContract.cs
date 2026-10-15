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
    /// permet de decouvrir un candidat, pas de conclure sur le jeu.</summary>
    Entrainement,

    /// <summary>Menu, lobby, ecran de fin. Le PC n'y fait pas le travail d'une partie, et un
    /// gain trouve la ne dit rien de ce que le joueur ressentira.</summary>
    MenuLobby,

    /// <summary>aucun jeu ne tournait : machine au repos, bureau, premier scan.
    ///
    /// A ne jamais confondre avec <see cref="Unknown"/> : celui-la est une ignorance,
    /// celui-ci est un fait que la personne connait. Un premier scan se fait presque toujours ici,
    /// et sans ce cas le produit n'aurait aucune case pour le dire : il faudrait s'accuser de ne
    /// pas savoir. La consequence est la meme (aucun profil ne se defend au repos), la phrase ne
    /// l'est pas : on ne reclame pas a quelqu'un de declarer ce qu'il vient de declarer.</summary>
    HorsJeu,
}

/// <summary>A quoi sert cette session dans la vie d'un profil.</summary>
public enum SceneRole
{
    /// <summary>Decouverte. On cherche un candidat, donc on prefere une scene stable, quitte a
    /// ce qu'elle ne represente qu'un coin de la carte.</summary>
    Decouverte,

    /// <summary>Validation. On rejoue le profil deja trouve sur une scene plus large, pour
    /// verifier qu'on n'a pas optimise un coin calme.</summary>
    Validation,
}

/// <summary>
/// Ou la mesure a ete prise, et ce que ca autorise a en conclure.
///
/// Une « partie reelle, zone calme » est une vraie scene de gameplay, bien meilleure qu'un menu.
/// Sans ce contrat, le profil ne le dirait nulle part, et une session prise dans un lobby serait
/// traitee exactement pareil.
///
/// ══ decouverte puis validation ══
/// Une zone calme est le bon endroit pour trouver un candidat : elle bruite peu, donc elle resout
/// mieux. C'est le mauvais endroit pour conclure que le profil vaut pour tout Warzone. Le modele
/// separe donc les deux roles : un candidat se decouvre ou l'on voit clair, et se valide ou l'on
/// joue vraiment.
///
/// `KeepDurable` dit si un keep est defendable a long terme ; il ne va pas chercher une seconde
/// scene tout seul. Un profil dit au moins l'endroit ou il est ne.
/// </summary>
/// <param name="Representativite">Ce que la scene represente. Declare par le joueur ou par
/// l'appelant : DrDXT ne sait pas reconnaitre un lobby, et pretendre le contraire serait pire
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

    /// <summary>Un keep trouve ici est-il defendable durablement ?
    ///
    /// Non sur un menu ou un lobby : le PC n'y fait pas le travail d'une partie.
    /// Non sur une scene inconnue : on ne garde pas un reglage sur la foi d'un endroit dont on
    /// ignore tout.
    /// Non sur un entrainement seul : il decouvre, il ne conclut pas.
    /// Non sur une decouverte de gameplay tant qu'aucune validation n'a suivi : c'est exactement
    /// le piege du coin calme.</summary>
    public bool KeepDurable =>
        Representativite == SceneRepresentativity.Gameplay && Role == SceneRole.Validation;

    /// <summary>Ce qu'il manque pour rendre un keep durable, en une phrase, ou <c>null</c> quand
    /// il ne manque rien.</summary>
    public string? CeQuiManque => Representativite switch
    {
        SceneRepresentativity.MenuLobby => Docteur.T("SC.MenuLobby"),
        SceneRepresentativity.Unknown => Docteur.T("SC.Inconnue"),
        SceneRepresentativity.Entrainement => Docteur.T("SC.Entrainement"),
        SceneRepresentativity.HorsJeu => Docteur.T("SC.HorsJeu"),
        _ => Role == SceneRole.Validation ? null : Docteur.T("SC.Decouverte"),
    };
}
