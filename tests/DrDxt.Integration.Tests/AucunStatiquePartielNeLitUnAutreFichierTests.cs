using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Dans une classe partial, l'ordre d'initialisation des champs statiques n'est garanti qu'à l'intérieur d'un même
/// fichier. Un champ statique initialisé à partir d'un champ statique d'un autre fichier peut lire null (ou 0 pour un
/// type valeur, sans aucun avertissement) et faire tomber la classe à son premier usage. Couper un gros fichier par
/// thème déplace des membres : cette garde refuse la coupe qui sépare un initialiseur de ce qu'il lit.
/// </summary>
public sealed class AucunStatiquePartielNeLitUnAutreFichierTests
{
    private static readonly Regex ClassePartielle = new(@"\bpartial\s+(?:class|record|struct)\s+(\w+)");

    // Un champ statique (ou une constante) initialisé : son nom, puis tout l'initialiseur jusqu'au point-virgule final.
    private static readonly Regex ChampStatique = new(
        @"^    (?:\[[^\]]*\]\s*)*(?:public|private|internal|protected)(?:\s+(?:static|readonly|const|new))+\s+[^=;(]+?\s(\w+)\s*=\s*(.+?);\s*$",
        RegexOptions.Multiline | RegexOptions.Singleline);

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    internal static IReadOnlyList<string> Fautes(IEnumerable<(string Fichier, string Texte)> fichiers)
    {
        var parClasse = fichiers
            .SelectMany(f => ClassePartielle.Matches(f.Texte).Select(m => (Classe: m.Groups[1].Value, f.Fichier, f.Texte)))
            .GroupBy(x => (Path.GetDirectoryName(x.Fichier), x.Classe))
            .Where(g => g.Select(x => x.Fichier).Distinct().Count() > 1);

        var fautes = new List<string>();
        foreach (var famille in parClasse)
        {
            var champs = famille
                .SelectMany(x => ChampStatique.Matches(x.Texte)
                    .Where(m => Regex.IsMatch(m.Value.Split('=')[0], @"\b(static|const)\b"))
                    .Select(m => (Nom: m.Groups[1].Value, Init: m.Groups[2].Value, x.Fichier)))
                .ToList();
            foreach (var c in champs)
                foreach (var autre in champs.Where(a => a.Nom != c.Nom && a.Fichier != c.Fichier))
                    if (Regex.IsMatch(c.Init, $@"\b{Regex.Escape(autre.Nom)}\b"))
                        fautes.Add($"{Path.GetFileName(c.Fichier)} : {c.Nom} lit {autre.Nom} ({Path.GetFileName(autre.Fichier)})");
        }
        return fautes;
    }

    [Fact]
    public void Le_crible_voit_un_initialiseur_qui_lit_un_autre_fichier_et_laisse_le_meme_fichier()
    {
        const string tronc = "public sealed partial class X\n{\n"
            + "    internal static readonly IReadOnlyDictionary<string, string> LevierDuReglage = new Dictionary<string, string>()\n"
            + "    {\n        [\"a\"] = \"b\",\n    };\n}\n";
        const string part = "public sealed partial class X\n{\n"
            + "    internal static readonly IReadOnlyDictionary<string, string> LevierQuiAllume =\n"
            + "        new Dictionary<string, string>(LevierDuReglage) { [\"c\"] = \"d\" };\n}\n";

        Fautes([("d/X.cs", tronc), ("d/X.Changements.cs", part)]).Should().ContainSingle()
            .Which.Should().Contain("LevierQuiAllume lit LevierDuReglage");
        Fautes([("d/X.cs", tronc + part), ("d/X.Autre.cs", "public sealed partial class X\n{\n}\n")])
            .Should().BeEmpty("dans un même fichier, l'ordre de déclaration fixe l'ordre d'initialisation");
    }

    [Fact]
    public void Aucun_champ_statique_d_une_classe_partial_ne_lit_un_champ_d_un_autre_fichier()
    {
        var racine = Racine();
        var lus = new[] { "src", "tests", "tools", "server" }
            .SelectMany(d => FichiersDuDepot.Sous(Path.Combine(racine, d), "*.cs"))
            .Select(f => (Fichier: f, Texte: File.ReadAllText(f)))
            .ToList();
        lus.Count(f => ClassePartielle.IsMatch(f.Texte)).Should().BeGreaterThan(30, "le dépôt découpe ses gros fichiers en partial");

        Fautes(lus).Should().BeEmpty("l'ordre d'initialisation statique entre fichiers d'une classe partial n'est pas garanti");
    }
}
