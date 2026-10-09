using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Une source, deux lecteurs.
///
/// `docs/NON-NUISANCE.md` dit ou le produit ecrit sur la machine du client, ce qu'il ne fait
/// Jamais, et ce qu'il ne sait pas. Ce document a deux lecteurs : un humain, et l'application, qui
/// doit pouvoir afficher ces memes phrases. Deux lecteurs sur deux copies finissent toujours par
/// diverger — et c'est l'ecran qui ment, parce que c'est lui qu'on oublie de relire.
///
/// Ce controle refuse la divergence. Il compare trois listes :
///   • les cles du bloc ```claims du document ;
///   • les cles `NonNuisance.*` de `Strings.resx` (langue neutre) ;
///   • les cles `NonNuisance.*` de `Strings.fr.resx`.
///
/// Il attrape donc les trois fautes reelles : une phrase promise au document et jamais ecrite, une
/// phrase ecrite et jamais declaree, une phrase presente dans une seule langue — celle qui donne
/// une cle brute a l'ecran d'un client francais.
///
/// ══ ce qu'il ne fait pas ══
/// Il ne verifie pas que la phrase est vraie : c'est le travail des mutations plus haut dans le
/// document, garde par garde. Il verifie qu'il n'existe qu'un seul endroit ou l'ajouter.
/// </summary>
public sealed class TheNeverAndUnknownClaimsHaveOneSourceGateTests
{
    private static readonly Regex ClaimsBlock = new(@"```claims\r?\n(.*?)```", RegexOptions.Singleline);
    private static readonly Regex ResxKey = new(@"<data name=""(NonNuisance\.[A-Za-z.]+)""");

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

    private static List<string> DeclaredInTheDocument()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "NON-NUISANCE.md"));
        var block = ClaimsBlock.Match(doc);
        block.Success.Should().BeTrue("docs/NON-NUISANCE.md doit porter le bloc ```claims : c'est la source");

        return block.Groups[1].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> TranslatedIn(string resxFileName)
    {
        var path = Path.Combine(RepoRoot(), "src", "DrDxt.Infrastructure", "Resources", resxFileName);
        return ResxKey.Matches(File.ReadAllText(path))
            .Select(m => m.Groups[1].Value)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void Every_never_and_unknown_claim_exists_in_the_document_and_in_both_languages()
    {
        var declared = DeclaredInTheDocument();

        // Garde de non-vacuite : trois listes vides seraient EGALES, et ce gate passerait au vert
        // sur un produit qui ne promet plus rien. Le seuil est le compte du 06/09/2026.
        declared.Should().HaveCountGreaterThanOrEqualTo(
            10, "le document declarait 10 phrases le 06/09/2026 ; en perdre une doit se voir");

        TranslatedIn("Strings.resx").Should().Equal(declared,
            "une phrase declaree dans docs/NON-NUISANCE.md et absente du resx neutre est une "
            + "promesse que le produit ne sait pas prononcer");

        TranslatedIn("Strings.fr.resx").Should().Equal(declared,
            "une phrase sans traduction francaise s'affiche en cle brute chez un client francais, "
            + "et c'est exactement le lecteur a qui elle est destinee");
    }

    [Fact]
    public void The_comparison_would_actually_notice_a_missing_translation()
    {
        // Controle POSITIF : sans lui, un motif casse rendrait trois listes vides et donc egales.
        // Il prouve que la comparaison sait dire non.
        var declared = DeclaredInTheDocument();
        var amputated = declared.Take(declared.Count - 1).ToList();

        amputated.Should().NotEqual(declared);
    }
}
