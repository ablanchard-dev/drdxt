namespace DrDxt.Core;

/// <summary>
/// Le dossier des donnees de DrDXT (%LOCALAPPDATA%\DrDXT), en un seul endroit.
///
/// Douze fichiers le recalculaient chacun a la main. Au-dela de la repetition, c'etait un obstacle a la
/// preuve : photographier l'appli en build de developpement lancait un vrai scan qui ecrivait dans les
/// donnees du joueur (references memoire et processeur, journal). En DEBUG seulement, la variable
/// <see cref="VariableDeDeveloppement"/> redirige le dossier vers un chemin absolu jetable. Une version
/// publiee (Release) ne la lit jamais : pas de nouvelle facon de deplacer les donnees d'un joueur.
/// </summary>
public static class DossierDonnees
{
    public const string VariableDeDeveloppement = "DRDXT_DONNEES_DEV";

    public static string Racine() => Racine(Environment.GetEnvironmentVariable);

    /// <summary>La meme regle, la lecture de variable injectee : un test ne touche pas l'environnement du
    /// processus, que d'autres tests lisent en parallele.</summary>
    public static string Racine(Func<string, string?> lireVariable)
    {
#if DEBUG
        var redirige = lireVariable(VariableDeDeveloppement);
        if (!string.IsNullOrWhiteSpace(redirige) && Path.IsPathFullyQualified(redirige)) return redirige;
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrDXT");
    }

    public static string Chemin(params string[] parties) => Path.Combine([Racine(), .. parties]);
}
