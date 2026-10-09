using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// 25/09/2026 : 41 commentaires citaient des numéros d'étape d'un journal de travail qui n'est pas dans
/// le dépôt (un « t » suivi d'un numéro, entre parenthèses). Pour un lecteur du dépôt, ce sont des renvois
/// vers rien. Un commentaire se date ; il ne renvoie pas à un carnet absent.
/// </summary>
public sealed class AucunNumeroDeTourDansLesCommentairesTests
{
    internal static readonly Regex Tour = new(
        @"\(t\d{1,3}[\),\-]|revue de sécurité t\d|, t\d{2,3}\)|\b(du|en|au) t\d{2,3}\b|\bt\d{2,3}-t\d{2,3}\b");

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    /// <summary>La partie commentaire d'une ligne C# ; une ligne Markdown est prise entière.</summary>
    private static string Commentaire(string ligne, bool markdown)
    {
        if (markdown) return ligne;
        var i = ligne.IndexOf("//", StringComparison.Ordinal);
        return i < 0 ? string.Empty : ligne[i..];
    }

    [Fact]
    public void Aucun_commentaire_ne_renvoie_a_un_numero_de_tour()
    {
        // Témoins : les formes d'avant sont vues, le code `Plan(t1, t2, t3)` ne l'est pas.
        Tour.IsMatch(Commentaire("x = 1; // 25/09/2026 (t292) : la ligne", false)).Should().BeTrue();
        Tour.IsMatch(Commentaire("// 25/09/2026 (revue de sécurité t178) : ce fichier", false)).Should().BeTrue();
        Tour.IsMatch(Commentaire("var r = Plan(t1, t2, t3);", false)).Should().BeFalse();

        var racine = Racine();
        var sep = Path.DirectorySeparatorChar;
        var lus = new[] { "src", "tests", "tools", "installer", "research", "docs" }
            .Where(d => Directory.Exists(Path.Combine(racine, d)))
            .SelectMany(d => FichiersDuDepot.Sous(Path.Combine(racine, d)))
            .Where(f => !f.Contains($"{sep}Output{sep}"))
            .Where(f => Path.GetExtension(f) is ".cs" or ".md")
            // Ses témoins portent la forme interdite dans des chaînes qui contiennent « // ».
            .Where(f => Path.GetFileName(f) != nameof(AucunNumeroDeTourDansLesCommentairesTests) + ".cs")
            .ToList();
        lus.Should().HaveCountGreaterThan(400, "src seul compte plus de 450 fichiers .cs");

        var fautes = lus
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, i, l)))
            .Where(x => Tour.IsMatch(Commentaire(x.l, x.f.EndsWith(".md", StringComparison.Ordinal))))
            .Select(x => $"{Path.GetRelativePath(racine, x.f)}:{x.i + 1}")
            .ToList();

        fautes.Should().BeEmpty("un commentaire se date, il ne renvoie pas à un journal absent du dépôt");
    }
}
