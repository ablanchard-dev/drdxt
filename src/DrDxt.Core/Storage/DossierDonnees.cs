namespace DrDxt.Core;

/// <summary>
/// Le dossier des donnees de DrDXT (%LOCALAPPDATA%\DrDXT), en un seul endroit. En DEBUG seulement,
/// <see cref="VariableDeDeveloppement"/> le redirige vers un chemin absolu jetable, pour qu'un scan de developpement
/// n'ecrive pas dans les donnees du joueur ; une version publiee (Release) ne la lit jamais.
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
