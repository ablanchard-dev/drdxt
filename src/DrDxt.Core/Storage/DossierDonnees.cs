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

    /// <summary>Vrai quand la variable est posee et que les donnees vont quand meme ailleurs : chemin relatif, ou
    /// version publiee. Un outil qui ecrit s'arrete la-dessus plutot que d'ecrire dans les donnees du joueur
    /// en laissant croire qu'il les a epargnees.</summary>
    public static bool RedirectionDemandeeMaisIgnoree(Func<string, string?> lireVariable)
    {
        var demande = lireVariable(VariableDeDeveloppement);
        return !string.IsNullOrWhiteSpace(demande) && Racine(lireVariable) != demande;
    }

    public static string Chemin(params string[] parties) => Path.Combine([Racine(), .. parties]);
}
