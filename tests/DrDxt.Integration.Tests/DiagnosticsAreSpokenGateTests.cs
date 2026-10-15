using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Un diagnostic que le produit ne prononce jamais ne vaut rien.
///
/// FrameCapInferencer a été écrit, testé unitairement et testé par mutation pendant une nuit
/// entière sans qu'une seule ligne du produit l'appelle : le verdict ne vivait que dans une
/// sonde en ligne de commande. Le défaut même que cette nuit a servi à corriger était une valeur
/// que le produit lisait depuis des mois sans jamais en dire un mot. Rebâtir la même forme
/// une couche plus haut est l'erreur la plus facile à refaire dans ce dépôt.
///
/// Ce crible balaie la source et échoue quand une classe dont le nom promet une réponse
/// (Inferencer / Analyzer / Advisor / Hygiene / Origin / Judge) n'est consommée par rien sous
/// src/ ; outils et tests ne comptent pas, précisément parce qu'une sonde est l'endroit où ces classes meurent.
///
/// La consommation par interface compte : la première version de cet audit accusait BiosAdvisor,
/// enregistré dans le DI et consommé comme IBiosAdvisor par ScanViewModel. Un audit qui porte
/// des accusations fausses avec assurance est la maladie même qu'il doit attraper, donc le nom
/// de la classe et I&lt;Nom&gt; sont tous deux cherchés.
/// </summary>
public sealed class DiagnosticsAreSpokenGateTests
{
    /// <summary>
    /// Dette déclarée, cherchable et plafonnée (règle du dépôt). Chaque entrée est un vrai composant
    /// que le produit ne consomme pas encore, avec la raison. Ajouter une ligne ici est un acte
    /// délibéré : le compte ci-dessous doit monter dans le même commit, ce qui le rend visible.
    /// </summary>
    private static readonly Dictionary<string, string> KnownUnspoken = new()
    {
        // Liste vide : `BenchmarkRunner` applique `FrameSeriesHygiene` par run aux sept series
        // appariees, et `AttributionReseauService` (qui s'appuie sur `NetworkPathAnalyzer`) rend son
        // verdict, affiche par `ScanViewModel`. N'y remettre un nom qu'en retirant son branchement.
    };

    private static readonly Regex Promising = new(
        // `Verifier` ajoute : BiosLoopVerifier repondait a « ton changement BIOS
        // a-t-il pris ? », etait enregistre en DI, et n'etait resolu par personne --
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
        // branchement de `FrameSeriesHygiene`, il aurait autorise une nouvelle dette
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
    // Sans ce filtre, le gate se declarerait satisfait par un `<see cref="IBiosLoopVerifier"/>`
    // pose dans la doc d'un fichier voisin (verifie par mutation). Un composant dont la seule trace
    // dans le produit est qu'on PARLE de lui est exactement celui que ce gate doit accuser.
    //
    // Volontairement lexical et grossier : une chaine litterale contenant "//" perd sa fin de
    // ligne. Le cout est une sous-detection dans un cas rare, jamais une fausse accusation, et
    // c'est le bon sens pour un audit.
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
