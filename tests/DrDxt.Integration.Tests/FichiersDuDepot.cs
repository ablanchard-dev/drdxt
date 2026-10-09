namespace DrDxt.Integration.Tests;

/// <summary>
/// Parcourt les fichiers du depot sans jamais entrer dans la sortie de build.
///
/// 18/09/2026. Une douzaine de gardes de ce projet balayaient `src/**` (voire tout le
/// depot) avec `SearchOption.AllDirectories`, en filtrant `bin`/`obj`/`.git` apres coup.
/// Or le filtre arrive trop tard : l'enumeration est **deja descendue** dans ces dossiers.
/// Pendant une execution de toute la solution, MSBuild y cree et y supprime des fichiers
/// en continu — et une enumeration recursive dont un dossier disparait sous elle leve.
///
/// C'est le profil exact du rouge observe au tour 100 : **jamais en isolation** (25
/// executions du seul projet d'integration : 25 verts), une fois sur cinq environ quand
/// les 18 projets tournent ensemble. Ce n'est pas prouve — le rouge n'a jamais ete
/// nomme, et je ne peux pas dire que c'etait celui-la. Ce qui est sur, c'est que ces
/// gardes lisaient un chantier en activite alors qu'elles n'ont **rien a y faire** :
/// elles cherchent des sources.
///
/// Ici on elague avant de descendre. Aucun chemin ignore n'est jamais ouvert.
/// </summary>
internal static class FichiersDuDepot
{
    /// <summary>Ce qu'une garde de source n'a aucune raison de lire.</summary>
    internal static readonly string[] Ignores =
        ["bin", "obj", ".git", "node_modules", "TestResults", "publish", ".vs"];

    /// <summary>Les fichiers correspondant au motif, sous <paramref name="racine"/>, en sautant les dossiers de build.</summary>
    internal static IEnumerable<string> Sous(string racine, string motif = "*")
    {
        if (!Directory.Exists(racine)) yield break;

        foreach (var fichier in Directory.EnumerateFiles(racine, motif))
        {
            // 23/09/2026 : le projet temporaire de la compilation XAML (« X_abc123_wpftmp.csproj »)
            // naît À côté du vrai et disparaît aussitôt ; listé puis illisible, il faisait rougir
            // AucunOctetNulDansLesSourcesTests une fois de temps en temps (vu trois fois).
            if (Path.GetFileName(fichier).Contains("_wpftmp.", StringComparison.OrdinalIgnoreCase)) continue;
            yield return fichier;
        }

        foreach (var dossier in Directory.EnumerateDirectories(racine))
        {
            if (Ignores.Contains(Path.GetFileName(dossier), StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (var fichier in Sous(dossier, motif))
                yield return fichier;
        }
    }
}
