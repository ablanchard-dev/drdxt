namespace DrDxt.Core;

/// <summary>Ce que la scene mesuree represente reellement du jeu.</summary>
public enum SceneRepresentativity
{
    /// <summary>Personne n'a dit ce qui etait joue. Par defaut : un profil sur une scene inconnue ne se garde pas
    /// durablement.</summary>
    Unknown,

    /// <summary>Une vraie partie. La seule famille sur laquelle un profil se defend.</summary>
    Gameplay,

    /// <summary>Terrain d'entrainement, salle privee, mode tir : reproductible et pauvre, pour decouvrir, pas
    /// conclure.</summary>
    Entrainement,

    /// <summary>Menu, lobby, ecran de fin : le PC n'y fait pas le travail d'une partie.</summary>
    MenuLobby,

    /// <summary>Aucun jeu ne tournait : machine au repos, bureau, premier scan. Un fait connu, pas l'ignorance de
    /// <see cref="Unknown"/> : meme consequence (aucun profil ne se defend au repos), autre phrase, on ne reclame pas ce
    /// qui vient d'etre declare.</summary>
    HorsJeu,
}

/// <summary>A quoi sert cette session dans la vie d'un profil.</summary>
public enum SceneRole
{
    /// <summary>Decouverte : on cherche un candidat sur une scene stable, meme un coin de la carte.</summary>
    Decouverte,

    /// <summary>Validation : le profil rejoue sur une scene plus large, pour ne pas avoir optimise un coin calme.</summary>
    Validation,
}

/// <summary>
/// Ou la mesure a ete prise, et ce que ca autorise a en conclure : une « partie reelle, zone calme » n'est pas un lobby.
/// Une zone calme bruite peu et resout mieux, bon endroit pour decouvrir un candidat, mauvais pour conclure sur tout
/// Warzone : un candidat se decouvre ou l'on voit clair et se valide ou l'on joue vraiment. `KeepDurable` dit si un keep
/// se defend a long terme, sans chercher une seconde scene ; un profil dit au moins ou il est ne.
/// </summary>
/// <param name="Representativite">Ce que la scene represente, declare par le joueur ou l'appelant : DrDXT ne sait pas
/// reconnaitre un lobby.</param>
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

    /// <summary>Un keep trouve ici est-il defendable durablement ? Non sur un menu ou un lobby, une scene inconnue, un
    /// entrainement seul, ni sur une decouverte de gameplay sans validation (le piege du coin calme).</summary>
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
