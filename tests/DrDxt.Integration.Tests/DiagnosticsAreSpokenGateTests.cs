using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// A diagnostic the product never pronounces is worth nothing.
///
/// Found on 02/09/2026: FrameCapInferencer was written, unit-tested and mutation-tested during a
/// whole night, and not ONE line of product code called it — the verdict lived only in a
/// command-line probe. The very defect that night was spent fixing was a value the product had
/// been READING for months without ever saying a word about it. Building the same shape again,
/// one layer up, is the easiest mistake in this codebase to make twice.
///
/// This gate scans the source and fails when a class whose name promises an ANSWER
/// (Inferencer / Analyzer / Advisor / Hygiene / Origin / Judge) is consumed by nothing under
/// src/ — tools and tests do not count, precisely because a probe is where these go to die.
///
/// Consumption through an interface counts: the first version of this audit accused BiosAdvisor,
/// which is registered in DI and consumed as IBiosAdvisor by ScanViewModel. An audit that
/// produces confident false accusations is the same disease it is meant to catch, so both the
/// class name and I&lt;Name&gt; are searched.
/// </summary>
public sealed class DiagnosticsAreSpokenGateTests
{
    /// <summary>
    /// Declared, greppable, CAPPED debt (repo rule R5). Each entry is a real component that the
    /// product does not consume yet, with the reason. Adding a line here is a deliberate act:
    /// the count below must be raised in the same commit, which is what makes it visible.
    /// </summary>
    private static readonly Dictionary<string, string> KnownUnspoken = new()
    {
        // `FrameSeriesHygiene` est sortie de cette liste le 07/09/2026 : `BenchmarkRunner`
        // l'applique desormais, par run, aux SEPT series appariees. La dette a tenu du 02/09 au
        // 07/09 -- et ce gate est ce qui l'a rendue visible tout ce temps au lieu de la laisser
        // dormir. Ne pas la remettre sans retirer le branchement.
        // `NetworkPathAnalyzer` est sortie de cette liste le 10/09/2026, apres huit jours de
        // dette : `AttributionReseauService` lit la passerelle de la carte physique, mesure les
        // deux fenetres et rend le verdict avec son caveat, et `ScanViewModel` l'affiche. La
        // liste est vide — c'est le seul etat ou ce gate ne protege rien d'autre que lui-meme.
    };

    private static readonly Regex Promising = new(
        // `Verifier` ajoute le 02/09/2026 : BiosLoopVerifier repondait a « ton changement BIOS
        // a-t-il pris ? » depuis M3, etait enregistre en DI, et n'etait resolu par personne --
        // exactement la forme que ce gate existe pour attraper, avec un mot que le motif ne
        // couvrait pas encore.
        @"public\s+(?:static\s+)?(?:sealed\s+)?class\s+(\w*(?:Inferencer|Analyzer|Analyser|Hygiene|Origin|Advisor|Judge|Verifier)\w*)",
        RegexOptions.Compiled);

    [Fact]
    public void Every_diagnostic_the_product_computes_is_consumed_by_the_product()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var sources = SourceFiles(src).ToList();
        sources.Should().NotBeEmpty("le scan doit trouver du code, sinon ce test est vert pour rien");

        var declarations = new Dictionary<string, string>();
        foreach (var (path, text) in sources)
            foreach (Match m in Promising.Matches(text))
                declarations[m.Groups[1].Value] = path;

        declarations.Should().NotBeEmpty("au moins un diagnostic doit exister, sinon le motif est casse");

        var unspoken = declarations
            .Where(d => !IsConsumedUnderSrc(d.Key, d.Value, sources))
            .Select(d => d.Key)
            .OrderBy(x => x)
            .ToList();

        unspoken.Should().BeEquivalentTo(KnownUnspoken.Keys,
            "un diagnostic que le produit ne prononce pas ne sert a rien ; s'il est nouveau, " +
            "il faut le brancher ou l'inscrire explicitement dans KnownUnspoken avec son motif");
    }

    [Fact]
    public void The_declared_debt_stays_capped_and_justified()
    {
        // Le plafond DESCEND avec la dette, sinon il n'en est plus un : laisse a 2 apres le
        // branchement de `FrameSeriesHygiene` (07/09/2026), il aurait autorise une nouvelle dette
        // gratuite, en silence, a la place de celle qu'on vient de payer.
        KnownUnspoken.Should().HaveCountLessThanOrEqualTo(0,
            "la liste d'exceptions est plafonnee : la faire grossir doit etre un acte delibere");
        // `OnlyContain` ECHOUE sur une liste vide — la bibliotheque exige au moins un element.
        // Le jour ou la derniere dette est payee, l'assertion de justification tombait donc en
        // rouge pour la meilleure des raisons possibles : il n'y a plus rien a justifier. Formule
        // en NEGATIF, elle dit la meme chose et reste vraie a zero.
        KnownUnspoken.Values.Should().NotContain(reason => reason.Length <= 60,
            "une exception sans motif ecrit est une exception qu'on oubliera");
    }

    private static bool IsConsumedUnderSrc(string name, string declPath, List<(string Path, string Text)> sources)
    {
        var use = new Regex(@"\bI?" + Regex.Escape(name) + @"\b", RegexOptions.Compiled);
        var interfaceDecl = new Regex(@"interface\s+I" + Regex.Escape(name) + @"\b", RegexOptions.Compiled);

        foreach (var (path, text) in sources)
        {
            if (path == declPath) continue;
            // Enregistrer n'est pas consommer. Un fichier de cablage DI nomme tout le monde et
            // n'appelle personne : sa ligne d'enregistrement suffisait a faire passer pour
            // consomme un composant que rien ne resolvait.
            if (IsDiRegistration(path)) continue;

            // Parler de quelqu'un n'est pas l'appeler : on compare sur le CODE seul.
            var code = CodeOnly(text);
            // Le fichier qui DECLARE l'interface ne compte pas comme un consommateur.
            if (interfaceDecl.IsMatch(code) && use.Matches(code).Count <= 2) continue;
            if (use.IsMatch(code)) return true;
        }
        return false;
    }

    // Un fichier de cablage DI : il nomme tout le monde et n'appelle personne.
    private static bool IsDiRegistration(string path) =>
        Path.GetFileName(path).EndsWith("ServiceCollectionExtensions.cs", StringComparison.Ordinal);

    // Le code sans ses commentaires.
    //
    // Trouve le 02/09/2026 apres deux mutations survivantes : le gate se declarait satisfait par
    // un `<see cref="IBiosLoopVerifier"/>` pose dans la doc d'un fichier voisin. Un composant
    // dont la seule trace dans le produit est qu'on PARLE de lui est exactement celui que ce
    // gate doit accuser — il se lit comme branche et ne tourne nulle part.
    //
    // Volontairement lexical et grossier : une chaine litterale contenant "//" perd sa fin de
    // ligne. Le cout est une sous-detection dans un cas rare, jamais une fausse accusation, et
    // c'est le bon sens pour un audit (cf. le faux positif BiosAdvisor de la premiere version).
    private static string CodeOnly(string text)
    {
        var noBlocks = BlockComment.Replace(text, " ");
        return string.Join('\n', noBlocks.Split('\n').Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i < 0 ? line : line[..i];
        }));
    }

    private static readonly Regex BlockComment =
        new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static IEnumerable<(string Path, string Text)> SourceFiles(string root) =>
        FichiersDuDepot.Sous(root, "*.cs")
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(p => (p, File.ReadAllText(p)));

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
