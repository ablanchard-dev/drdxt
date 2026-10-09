using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// 25/09/2026 : une regex `\r?\n` écrite par un script shell est arrivée dans un test avec
/// un vrai retour chariot à la place de `\r`. Le test passait quand même, par accident. Un retour chariot
/// qui n'est pas suivi d'un saut de ligne n'a aucune raison d'être dans un fichier texte du dépôt : c'est
/// la trace d'un échappement mangé en route.
/// </summary>
public sealed class AucunRetourChariotIsoleTests
{
    private static readonly Regex CrIsole = new("\r(?!\n)");

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Fact]
    public void Aucun_fichier_texte_ne_contient_un_retour_chariot_isole()
    {
        // Témoin : la forme exacte de la faute du 25/09, et une fin de ligne Windows qui doit passer.
        CrIsole.IsMatch("MatchRegex(@\"x\\s*: 1\r?\n\");").Should().BeTrue();
        CrIsole.IsMatch("ligne\r\nligne\r\n").Should().BeFalse();

        var racine = Racine();
        var sep = Path.DirectorySeparatorChar;
        var lus = new[] { "src", "tests", "tools", "installer" }
            .SelectMany(d => FichiersDuDepot.Sous(Path.Combine(racine, d)))
            .Where(f => !f.Contains($"{sep}Output{sep}"))
            .Where(f => Path.GetExtension(f) is ".cs" or ".xaml" or ".resx" or ".json" or ".ps1" or ".iss" or ".txt" or ".csproj" or ".md" or ".py")
            .ToList();
        lus.Should().HaveCountGreaterThan(400, "src seul compte plus de 450 fichiers .cs");

        var fautes = lus
            .Where(f => CrIsole.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(racine, f))
            .ToList();

        fautes.Should().BeEmpty("un retour chariot isolé est un échappement mangé par un outil d'édition");
    }
}
