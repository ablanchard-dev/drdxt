namespace DrDxt.Detection;

/// <summary>
/// Un fichier écrit par un jeu ou un lanceur, lu avec un plafond : il n'est pas à DrDXT, et un fichier de plusieurs Go
/// viderait la mémoire. Les réglages, manifestes et listes que DrDXT lit font quelques Ko ; un format qui demande plus
/// garde son propre plafond (journaux d'OBS, rapports de plantage de Call of Duty).
/// </summary>
public static class FichierTiers
{
    public const long OctetsMax = 1L << 20;

    /// <summary>Le texte, comme <see cref="File.ReadAllText(string)"/> (encodage lu sur la marque d'ordre des octets).
    /// Lève <see cref="IOException"/> au-delà du plafond, compté à la lecture : un fichier qui grandit pendant qu'on le lit ne
    /// le passe pas. Lecture partagée : le jeu peut l'avoir ouvert.</summary>
    public static string Lire(string chemin)
    {
        using var fs = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var lu = new MemoryStream();
        var bloc = new byte[81920];
        for (int n; (n = fs.Read(bloc)) > 0;)
        {
            if (lu.Length + n > OctetsMax) throw new IOException($"{chemin} : plus de {OctetsMax} octets, non lu.");
            lu.Write(bloc, 0, n);
        }
        lu.Position = 0;
        using var r = new StreamReader(lu);
        return r.ReadToEnd();
    }
}
