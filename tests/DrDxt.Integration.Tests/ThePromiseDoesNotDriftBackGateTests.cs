using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// D1 / D3 — la promesse ne redérive pas.
///
/// Le produit a changé de promesse le 24/08. Ce n'est plus « plus de FPS », c'est :
///
///   « On trouve les erreurs de configuration qui te coûtent vraiment des images, on le prouve
///     par la mesure, on ne casse rien, et ça survit aux mises à jour Windows. Si ta machine est
///     déjà bonne, on te le prouve aussi. »
///
/// Pourquoi ça compte au point d'avoir un gate : le concurrent le plus radical du marché —
/// Atlas OS, qui réinstalle un Windows entier — a été mesuré à **444 FPS contre 444**, 1 % low
/// 403 contre 401,5, latence 11,4 ms contre 11,3. Aucune différence. Vendre du FPS sur ce marché,
/// c'est se faire démentir par son propre instrument au moment exact où le client le lance.
///
/// Une promesse ne se tient pas dans un plan : elle se tient dans les chaînes que le produit
/// affiche. Et une chaîne se réécrit en dix secondes, six mois plus tard, par quelqu'un qui
/// voulait juste « rendre ça plus vendeur ». D'où ce gate.
///
/// Il interdit aussi la clause de remboursement conditionnelle (D3). L'ancienne — « remboursé
/// si zéro gain mesurable » — remboursait systématiquement les clients les MIEUX équipés (une
/// machine de joueur a souvent déjà XMP activé) et transformait chaque demande en discussion sur
/// la marge d'erreur. Une garantie doit CLORE un litige, pas en ouvrir un.
/// </summary>
public sealed class ThePromiseDoesNotDriftBackGateTests
{
    /// <summary>Ce qu'aucune chaîne affichée ne doit dire, et pourquoi.</summary>
    private static readonly (Regex Pattern, string Why)[] Forbidden =
    {
        (new Regex(@"\+\s*\d+\s*(%|fps)", RegexOptions.IgnoreCase),
            "un gain chiffre promis d'avance : le produit MESURE un gain, il n'en annonce jamais un"),

        (new Regex(@"\b(jusqu'a|jusqu'à|up to)\b.{0,20}\bfps\b", RegexOptions.IgnoreCase),
            "« jusqu'a X FPS » est la formule des optimiseurs que ce produit refuse d'etre"),

        (new Regex(@"\b(gain|gains)\s+(garanti|garantis|guaranteed)\b", RegexOptions.IgnoreCase),
            "aucun gain n'est garanti : sur une machine deja bonne il n'y en a pas, et c'est un resultat valide"),

        (new Regex(@"\b(rembours\w*|refund\w*)\b.{0,40}\b(gain|mesur\w*|measur\w*)\b", RegexOptions.IgnoreCase),
            "D3 : la garantie est INCONDITIONNELLE. La conditionner a un gain mesure rembourse les "
            + "clients les mieux equipes et ouvre un litige au lieu de le clore"),

        (new Regex(@"\b(gain|mesur\w*|measur\w*)\b.{0,40}\b(rembours\w*|refund\w*)\b", RegexOptions.IgnoreCase),
            "meme clause, dans l'autre sens"),

        (new Regex(@"\b(booste|boostez|boost your|supercharge|turbo)\b", RegexOptions.IgnoreCase),
            "vocabulaire d'optimiseur : le produit constate et prouve, il ne survend pas"),
    };

    public static TheoryData<string> ResxFiles()
    {
        var dir = Path.Combine(RepoRoot(), "src", "DrDxt.Infrastructure", "Resources");
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "Strings*.resx")) data.Add(f);
        return data;
    }

    [Theory]
    [MemberData(nameof(ResxFiles))]
    public void No_displayed_string_promises_what_the_product_refuses_to_promise(string path)
    {
        var offenders = new List<string>();

        foreach (var (key, value) in Entries(path))
        foreach (var (pattern, why) in Forbidden)
        {
            if (pattern.IsMatch(value))
                offenders.Add($"{key} : « {value} » — {why}");
        }

        offenders.Should().BeEmpty(
            "la promesse du produit vit dans ces chaines, et elle se reecrit en dix secondes");
    }

    [Fact]
    public void The_two_languages_both_carry_the_promise()
    {
        // Une promesse presente dans une seule langue n'engage que la moitie des clients.
        foreach (var path in ResxFiles().Select(row => (string)row[0]!))
        {
            var keys = Entries(path).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            keys.Should().Contain("Product.Promise", $"{Path.GetFileName(path)} doit porter la promesse");
        }
    }

    /// <summary>Un chiffre de gain, quel qu'il soit, dans une chaine litterale du produit.</summary>
    private static readonly Regex GainFigure =
        new(@"(\+\s*\d+\s*(%|fps)|\b(jusqu'a|jusqu'à|up to)\b.{0,20}(\d+\s*(%|fps)))", RegexOptions.IgnoreCase);

    /// <summary>
    /// Ce qui transforme un chiffre nu en chiffre SOURCE. Deux formes, parce que le produit en
    /// utilise deux : une reference textuelle (CAHIER, research/…md, une URL) et une provenance
    /// STRUCTURELLE — la constante `Src…` passee a cote du texte, avec `GainConfidence.Sourced`.
    /// La seconde a ete trouvee en accusant a tort une chaine parfaitement sourcee.
    /// </summary>
    private static readonly Regex SourceMarker =
        new(@"(CAHIER|research/|\.md\b|https?://|TechPowerUp|DECISION-|GainConfidence\.Sourced|\bSrc[A-Z]\w*)",
            RegexOptions.IgnoreCase);

    [Fact]
    public void A_gain_figure_in_the_code_never_travels_without_its_source()
    {
        // Mesure du 05/09/2026 : ce gate ne lisait que les .resx. Or `DiagnosticReport` porte 47
        // chaines en dur que le CLIENT lit dans son ticket, et les `GainEvidence` du catalogue de
        // profils sont surfacees par l'UI (ScanProfileConsumer.cs:25). Deux chiffres de gain
        // vivaient donc hors de portee du gate.
        //
        // Ils sont legitimes : ils citent leur source (CAHIER §5, research/…md). C'est exactement
        // la doctrine du produit — « jamais un point-% promis nu » (BiosContracts.cs:100). Ce qui
        // manquait, c'est le controle : rien n'empechait le PROCHAIN chiffre d'etre nu.
        //
        // Aucune liste de fichiers ici : on balaie la production et on exige la source. Une liste
        // ecrite a la main derive, c'est le defaut trouve deux fois dans cette meme session.
        // La source vit dans l'EXPRESSION, pas forcement dans le morceau : une evidence sourcee
        // s'ecrit sur plusieurs lignes concatenees, et le « research/…md » peut etre trois lignes
        // plus haut. Juger le fragment isole accuserait une chaine parfaitement sourcee — mesure
        // faite : c'est exactement ce que la premiere version de ce controle a fait.
        const int Window = 4;
        var literal = new Regex("\"([^\"\n]{4,})\"", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in ProductionSources())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in literal.Matches(lines[i]))
                {
                    var value = m.Groups[1].Value;
                    if (!GainFigure.IsMatch(value)) continue;

                    var from = Math.Max(0, i - Window);
                    var to = Math.Min(lines.Length - 1, i + Window);
                    var context = string.Join('\n', lines[from..(to + 1)]);

                    if (!SourceMarker.IsMatch(context))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1} : « {value} »");
                }
            }
        }

        offenders.Should().BeEmpty(
            "un chiffre de gain sans sa source est une promesse ; avec sa source, c'est une mesure "
            + "faite ailleurs, et le produit a le droit de la citer en le disant");
    }

    private static IEnumerable<string> ProductionSources() =>
        FichiersDuDepot.Sous(Path.Combine(RepoRoot(), "src"), "*.*")
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(
                seg => seg.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || seg.Equals("obj", StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void The_gate_would_actually_catch_a_forbidden_claim()
    {
        // Controle POSITIF : sans lui, une expression reguliere cassee donnerait la meme couleur
        // qu'un depot sain — c'est deja arrive deux fois dans cette session.
        var samples = new[]
        {
            "Gagnez jusqu'a 30 FPS sur vos jeux",
            "+15% de performances garanties",
            "Rembourse si aucun gain mesure",
            "Boostez votre PC",
        };

        foreach (var sample in samples)
            Forbidden.Any(f => f.Pattern.IsMatch(sample))
                .Should().BeTrue($"« {sample} » doit etre attrape");
    }

    private static IEnumerable<(string Key, string Value)> Entries(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .Select(d => (Key: d.Attribute("name")?.Value ?? "", Value: d.Element("value")?.Value ?? ""))
            .Where(e => e.Key.Length > 0);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }
}
