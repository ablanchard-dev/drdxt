using System.Text.RegularExpressions;

namespace DrDxt.Application.Support;

/// <summary>
/// « Quoi de neuf » : ce qui a changé pour le joueur depuis la dernière version qu'il a vue, montré une fois. Le texte vient du
/// CHANGELOG livré dans le binaire signé (ressource de l'App), jamais téléchargé à part : il ne peut pas dire autre chose que
/// la version installée. Seuls les points sous « Pour le joueur » (« For the player » en anglais) sont repris ; le reste
/// s'adresse aux contributeurs, et une section qui n'en a pas ne montre rien. Chaque langue a son fichier, lu par la même
/// fonction : le français pour un joueur qui lit le français, l'anglais pour tous les autres.
/// </summary>
public static class NotesDeVersion
{
    public sealed record Section(string Version, string Titre, IReadOnlyList<string> Points);

    private static readonly Regex TitreDeVersion = new(@"^##\s+(?<v>\d+\.\d+\.\d+)\b.*$", RegexOptions.Multiline);

    /// <summary>Pur : la ressource du CHANGELOG que l'App lit dans cette langue.</summary>
    public static string Ressource(System.Globalization.CultureInfo langue)
        => Francais(langue) ? "CHANGELOG.md" : "CHANGELOG.en.md";

    private static bool Francais(System.Globalization.CultureInfo langue) => langue.TwoLetterISOLanguageName == "fr";

    /// <summary>Pur : les sections du CHANGELOG (« ## 0.9.1 (date) »), la plus récente d'abord, avec leurs points pour le joueur
    /// dans cette langue.</summary>
    public static IReadOnlyList<Section> Sections(string changelog, System.Globalization.CultureInfo langue)
    {
        var titres = TitreDeVersion.Matches(changelog.Replace("\r\n", "\n")).ToList();
        var texte = changelog.Replace("\r\n", "\n");
        var sections = new List<Section>();
        for (var i = 0; i < titres.Count; i++)
        {
            var debut = titres[i].Index + titres[i].Length;
            var corps = texte[debut..(i + 1 < titres.Count ? titres[i + 1].Index : texte.Length)];
            var joueur = corps.IndexOf(Francais(langue) ? "Pour le joueur" : "For the player", StringComparison.Ordinal);
            corps = joueur >= 0 ? corps[joueur..] : "";
            var fin = Regex.Match(corps, @"\n[^-\s][^\n]*:\s*\n").Index is var f and > 0 ? f : corps.Length;
            var points = new List<string>();
            foreach (var ligne in corps[..fin].Split('\n'))
            {
                if (ligne.StartsWith("- ", StringComparison.Ordinal)) points.Add(ligne[2..].Trim());
                else if (ligne.StartsWith("  ", StringComparison.Ordinal) && points.Count > 0 && ligne.Trim().Length > 0)
                    points[^1] += " " + ligne.Trim();
            }
            sections.Add(new Section(titres[i].Groups["v"].Value, titres[i].Value.TrimStart('#', ' '), points));
        }
        return sections;
    }

    /// <summary>Pur : ce que l'écran montre, depuis le CHANGELOG de la langue du joueur, et jamais une section sans point
    /// pour le joueur.</summary>
    public static IReadOnlyList<Section> AMontrer(string changelog, string? versionVue, string? versionInstallee,
        System.Globalization.CultureInfo langue)
        => NonVues(Sections(changelog, langue), versionVue, versionInstallee).Where(s => s.Points.Count > 0).ToList();

    /// <summary>Pur : la version vue gardée (une chaîne JSON) ; <c>null</c> si absente ou illisible, comme au premier lancement.</summary>
    public static string? VersionVue(string? json)
    {
        try { return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<string>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Pur : les sections plus récentes que la version vue. Rien de vu (premier lancement) : la version installée seule.</summary>
    public static IReadOnlyList<Section> NonVues(IReadOnlyList<Section> sections, string? versionVue, string? versionInstallee)
    {
        if (versionVue is null)
            return sections.Where(s => s.Version == versionInstallee).Take(1).ToList();
        return Version.TryParse(versionVue, out var vue)
            ? sections.Where(s => Version.TryParse(s.Version, out var v) && v > vue).ToList()
            : [];
    }
}
