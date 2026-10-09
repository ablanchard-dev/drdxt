using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Le 24/09/2026, 1 030 pictogrammes (dont une partie dans des textes affichés) et 17 marqueurs
/// « ponytail: » ont été retirés du code. Le dépôt est destiné à être public : ce sont des traces
/// d'écriture générée, pas du code. Ce test fait échouer leur retour dans `src`.
/// </summary>
public sealed class SourceSansTraceDeGenerationTests
{
    // Plages des pictogrammes, comparées par valeur : ce fichier ne doit pas en contenir lui-même.
    private static bool Pictogramme(string ligne) => ligne.EnumerateRunes().Any(r => r.Value is (>= 0x1F300 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or 0x2B50 or 0x2B55);
    private static readonly Regex Marqueur = new(@"\bponytail\b", RegexOptions.IgnoreCase);

    // PUB2 (08/10/2026) : le dépôt devient public ; les noms internes du travail (l'agent de revue, les fichiers de boucle) n'y
    // ont pas leur place. Sensible à la casse : « fable » et « loop » sont aussi des mots courants.
    // Audit du 09/10/2026 : le prénom du mainteneur était revenu dans 4 fichiers de src ; le prénom du mainteneur n'a rien à y faire.
    private static readonly Regex NomInterne = new(@"\bFable\b|LOOP-DRDXT|\bAlex\b");
    // Les mots d'insistance les plus fréquents avant le 24/09/2026 (never 194, only 159...), en
    // capitales dans un commentaire. Le bandeau de sécurité des clés garde les siennes : c'est un
    // contrat vérifié par SecurityGateTests, pas un cri.
    private static readonly HashSet<string> BandeauxDeSecurite = ["UpdateDevKeys.cs", "ManifestDevKeys.cs"];
    private static readonly Regex Cri = new(@"//.*\b(NEVER|ONLY|JAMAIS|AVANT|SAME|REAL)\b");

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Fact]
    public void Aucun_pictogramme_ni_marqueur_d_outil_dans_src()
    {
        var src = Path.Combine(Racine(), "src");
        var fichiers = new[] { "*.cs", "*.xaml", "*.resx", "*.hlsl" }
            .SelectMany(motif => Directory.EnumerateFiles(src, motif, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var fautes = new List<string>();
        foreach (var f in fichiers)
        {
            var lignes = File.ReadAllLines(f);
            for (var i = 0; i < lignes.Length; i++)
                if (Pictogramme(lignes[i]) || Marqueur.IsMatch(lignes[i]) || NomInterne.IsMatch(lignes[i])
                    || (Cri.IsMatch(lignes[i]) && !BandeauxDeSecurite.Contains(Path.GetFileName(f))))
                    fautes.Add($"{Path.GetRelativePath(src, f)}:{i + 1}");
        }

        // Témoin de lecture : un dossier déplacé rendrait ce crible vert sur zéro fichier.
        fichiers.Should().HaveCountGreaterThan(300, "le crible doit avoir lu le vrai code du produit");
        fautes.Should().BeEmpty();
    }

    // Les outils du dépôt, publics eux aussi. `ages.py` lit la coche des registres : c'est une
    // donnée, pas une trace.
    [Fact]
    public void Aucun_pictogramme_dans_les_outils()
    {
        var tools = Path.Combine(Racine(), "tools");
        string[] extensions = [".cs", ".py", ".ps1", ".c", ".csproj", ".md"];
        var fichiers = Directory.EnumerateFiles(tools, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f))
                     && Path.GetFileName(f) != "ages.py"
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}"))
            .ToList();

        var fautes = fichiers.SelectMany(f => File.ReadAllLines(f)
                .Select((l, i) => (l, i))
                .Where(x => Pictogramme(x.l) || Marqueur.IsMatch(x.l))
                .Select(x => $"{Path.GetRelativePath(tools, f)}:{x.i + 1}"))
            .ToList();

        fichiers.Should().HaveCountGreaterThan(50, "le crible doit avoir lu les vrais outils");
        fautes.Should().BeEmpty();
    }
}
