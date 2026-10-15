using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Network;

/// <summary>Ce que le joueur a allume : le jeu mesure et le plafond que sa mesure a donne.</summary>
public sealed record PlafondEnvoiRetenu(string Exe, long PlafondBps, DateTimeOffset MesureLe);

/// <summary>
/// Le mode « proteger ton ping » survit a la fermeture de DrDXT.
///
/// Il vivait dans la memoire de l'appli : la fermer l'eteignait, la rouvrir ne le rallumait pas. Le clic
/// du joueur est le consentement ; ce fichier le garde jusqu'a ce qu'il eteigne le mode.
///
/// Il vit sous %LOCALAPPDATA%, ecrivable par tout processus du compte. Ce qu'il porte repasse de toute
/// facon par l'allowlist du helper (nom d'exe par regex, plafond borne) : le pire qu'un code du meme
/// compte puisse en tirer est de brider son propre envoi.
/// </summary>
public sealed class PlafondEnvoiMemorise
{
    public const string CodeErreur = "PLAFOND_ENVOI_MEMORISE";
    private readonly FichierJsonAtomique _fichier;

    public PlafondEnvoiMemorise(string chemin) => _fichier = new FichierJsonAtomique(chemin, CodeErreur);

    public static PlafondEnvoiMemorise ParDefaut() => new(FichierJsonAtomique.CheminLocal("plafond-envoi.json"));

    public Result Retenir(PlafondEnvoiRetenu retenu) => _fichier.WriteAtomic(JsonSerializer.Serialize(retenu));

    /// <summary>Le joueur a eteint le mode. On ecrit « rien » plutot que de supprimer : meme chemin
    /// atomique, et un fichier absent ou vide se lit pareil.</summary>
    public Result Oublier() => _fichier.WriteAtomic("null");

    /// <summary>Le mode a rallumer, ou <c>null</c> : absent, eteint, illisible ou incoherent. Un
    /// fichier abime n'allume jamais rien.</summary>
    public PlafondEnvoiRetenu? Lire()
    {
        var texte = _fichier.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<PlafondEnvoiRetenu>(texte.Value);
            return r is { Exe.Length: > 0, PlafondBps: > 0 } && EstUnJeuPossible(r.Exe) ? r : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // La regle vit dans Core (NomExeDeJeu.EstUnJeuPossible), lue aussi par le helper et le service.
    public static bool EstUnJeuPossible(string exe) => NomExeDeJeu.EstUnJeuPossible(exe);
}
