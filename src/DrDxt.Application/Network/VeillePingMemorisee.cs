using DrDxt.Core;

namespace DrDxt.Application.Network;

/// <summary>
/// « Surveiller mes parties » survit a la fermeture de DrDXT, comme la protection du ping
/// (<see cref="PlafondEnvoiMemorise"/>). Le clic du joueur est le consentement ; ce fichier le garde jusqu'a
/// ce qu'il arrete la veille, ou que la veille trouve un plafond (la memoire du plafond prend alors le relais).
///
/// Un seul mot : <c>true</c>. Tout autre contenu (absent, abime, « false ») se lit « eteinte » : la veille ne
/// pose rien par elle-meme, mais un fichier abime ne rallume jamais un releve de pings.
/// </summary>
public sealed class VeillePingMemorisee
{
    public const string CodeErreur = "VEILLE_PING_MEMORISEE";
    private readonly FichierJsonAtomique _fichier;

    public VeillePingMemorisee(string chemin) => _fichier = new FichierJsonAtomique(chemin, CodeErreur);

    public static VeillePingMemorisee ParDefaut() => new(FichierJsonAtomique.CheminLocal("veille-ping.json"));

    public Result Allumer() => _fichier.WriteAtomic("true");

    public Result Eteindre() => _fichier.WriteAtomic("false");

    public bool EstAllumee()
    {
        var texte = _fichier.Read();
        return texte.IsOk && texte.Value?.Trim() == "true";
    }
}
