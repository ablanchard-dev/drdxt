namespace DrDxt.Detection;

/// <summary>
/// Un chemin lu dans une valeur que tout programme du compte peut écrire (registre de l'utilisateur, tâche planifiée, liste
/// de démarrage) ne se suit que s'il est local : une lettre de lecteur, puis un séparateur. Un partage réseau
/// (\\hôte\…, \\?\UNC\…) ouvrirait une connexion SMB, donc une authentification du compte vers l'hôte nommé, au premier
/// File.Exists.
/// </summary>
public static class CheminsLocaux
{
    public static bool EstLocal(string? chemin)
        => chemin is { Length: >= 3 } c && char.IsAsciiLetter(c[0]) && c[1] == ':' && c[2] is '\\' or '/';
}
