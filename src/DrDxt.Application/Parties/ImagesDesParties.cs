using System.Globalization;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>
/// Option de développement : les temps de trame de chaque fenêtre d'une partie relevée, et si le relevé la compte jouée
/// (<see cref="ReleveDesParties.EstJouee"/>). Le relevé ne garde qu'un résumé par partie ; sans les images, les deux
/// estimateurs du 1 % low (toutes les images des fenêtres jouées, ou médiane par fenêtre) ne se comparent pas sur de vraies
/// parties. N'existe qu'en build de développement, et seulement quand les données vont au dossier jetable
/// (<see cref="DossierDonnees.VariableDeDeveloppement"/>) : une version publiée n'écrit jamais ces fichiers.
/// </summary>
public static class ImagesDesParties
{
    public const string SousDossier = "images-parties";
    private const string CodeErreur = "IMAGES_PARTIES";

    /// <param name="buildDeDeveloppement">Vrai seulement dans une build DEBUG (l'appelant passe la constante).</param>
    public static bool Activee(bool buildDeDeveloppement, Func<string, string?> lireVariable)
        => buildDeDeveloppement && !string.IsNullOrWhiteSpace(lireVariable(DossierDonnees.VariableDeDeveloppement));

    private sealed record Fenetre(DateTimeOffset Quand, bool Jouee, double Fps, double Low1, IReadOnlyList<double>? ImagesMs);
    private sealed record Fichier(string Jeu, DateTimeOffset Debut, DateTimeOffset Fin, bool Low1SurToutesLesImages, double Low1Median,
        IReadOnlyList<Fenetre> Fenetres);

    /// <summary>Écrit un fichier par partie dans <paramref name="dossier"/> et rend son chemin.</summary>
    public static string Ecrire(string dossier, PartieRelevee partie, IReadOnlyList<CaptureEnPartie> captures)
    {
        var reference = ReleveDesParties.FpsDeReference(captures);
        var fichier = new Fichier(partie.Jeu, partie.Debut, partie.Fin, partie.Low1SurToutesLesImages, partie.Low1Median,
            captures.Select(c => new Fenetre(c.Quand, ReleveDesParties.EstJouee(c, reference), c.Fps, c.Low1, c.ImagesMs)).ToList());
        var jeu = string.Concat(partie.Jeu.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var chemin = Path.Combine(dossier,
            $"images-{partie.Debut.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{jeu}.json");
        // Jamais d'écriture en place dans le dossier de données : le renommage remplace le nom, il ne suit pas un lien posé d'avance.
        var ecrit = new FichierJsonAtomique(chemin, CodeErreur).WriteAtomic(JsonSerializer.Serialize(fichier));
        if (!ecrit.IsOk) throw new IOException(ecrit.Error?.Message);
        return chemin;
    }
}
