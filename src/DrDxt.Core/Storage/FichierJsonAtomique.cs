namespace DrDxt.Core;

/// <summary>
/// Un fichier JSON ecrit sans jamais etre lu a moitie.
///
/// Fichier temporaire a cote de la cible (meme volume, donc le deplacement est un vrai renommage),
/// puis remplacement. Un lecteur ne voit jamais un fichier a demi ecrit, et **une reference tronquee
/// serait pire qu'absente** : elle se comparerait quand meme.
///
/// Le piege que cette forme evite : ouvrir la cible en ecriture la
/// tronque avant d'ecrire quoi que ce soit. Une panne a cet instant precis laisse un fichier vide
/// la ou il y avait une mesure.
///
/// Vit dans Core parce que cinq magasins en ont besoin (references memoire et CPU, journal de
/// performance, journal des constats, marqueur BIOS) : une correction faite ici les atteint tous. Toutes les frontieres sont enveloppees : aucune exception ne remonte.
/// </summary>
public sealed class FichierJsonAtomique
{
    private readonly string _chemin;
    private readonly string _codeErreur;

    /// <param name="chemin">La cible.</param>
    /// <param name="codeErreur">Le code que porteront ses echecs. Chaque magasin garde le sien :
    /// un appelant qui distingue ses erreurs par leur code ne doit pas les voir fusionner.</param>
    public FichierJsonAtomique(string chemin, string codeErreur)
    {
        _chemin = string.IsNullOrWhiteSpace(chemin)
            ? throw new ArgumentException("A path is required.", nameof(chemin))
            : chemin;
        _codeErreur = codeErreur;
    }

    /// <summary>Un chemin sous le dossier local de l'utilisateur.</summary>
    public static string CheminLocal(string nomFichier)
        => DrDxt.Core.DossierDonnees.Chemin(nomFichier);

    public Result WriteAtomic(string json)
    {
        try
        {
            var dossier = Path.GetDirectoryName(_chemin);
            if (!string.IsNullOrEmpty(dossier) && !Directory.Exists(dossier))
                Directory.CreateDirectory(dossier);

            var tmp = _chemin + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _chemin, overwrite: true);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(_codeErreur, $"Ecriture impossible : {ex.Message}");
        }
    }

    public Result<string?> Read()
    {
        try
        {
            return File.Exists(_chemin)
                ? Result<string?>.Ok(File.ReadAllText(_chemin))
                : Result<string?>.Ok(null);
        }
        catch (Exception ex)
        {
            return Result<string?>.Fail(_codeErreur, $"Lecture impossible : {ex.Message}");
        }
    }
}
