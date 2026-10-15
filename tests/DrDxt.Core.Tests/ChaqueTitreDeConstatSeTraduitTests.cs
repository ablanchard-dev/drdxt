using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Le nom d'un constat sert de clé au journal et, par « Nom.<nom> » dans Docteur.resx, de titre affiché. Un nom français
// sans cette clé s'affiche en français dans l'interface anglaise (« Version du pilote graphique » sur un Windows
// anglais). Les noms écrits comme un identifiant ou un nom propre (AllowAutoGameMode, Curve Optimizer (AMD)) restent tels quels.
public sealed class ChaqueTitreDeConstatSeTraduitTests
{
    private static readonly Regex NomLitteral = new(@"new RepairFinding\(\s*[^,()]+,\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex NomConstant = new(@"new RepairFinding\(\s*[^,()]+,\s*Nom\b", RegexOptions.Compiled);
    private static readonly Regex ConstanteNom = new(@"const string Nom = ""([^""]+)""", RegexOptions.Compiled);

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static bool EnPhrase(string nom) => nom.Contains(' ') && char.IsLower(nom[0]);

    [Fact]
    public void Chaque_nom_ecrit_en_phrase_a_son_titre_dans_les_deux_langues()
    {
        var racine = Racine();
        var noms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(racine, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var code = File.ReadAllText(f);
            foreach (Match m in NomLitteral.Matches(code)) noms.Add(m.Groups[1].Value);
            if (NomConstant.IsMatch(code) && ConstanteNom.Match(code) is { Success: true } c) noms.Add(c.Groups[1].Value);
        }
        noms.Should().Contain("en attente", "témoin : le crible lit bien les constats");

        foreach (var fichier in new[] { "Docteur.resx", "Docteur.fr.resx" })
        {
            var resx = File.ReadAllText(Path.Combine(racine, "src", "DrDxt.Core", "Resources", fichier));
            noms.Where(EnPhrase).Where(n => !resx.Contains($"<data name=\"Nom.{n}\"", StringComparison.Ordinal))
                .Should().BeEmpty($"chaque nom de constat écrit en phrase a son titre dans {fichier}");
        }
    }
}
