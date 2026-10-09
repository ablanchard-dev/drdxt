using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Un chiffre sans source est une estimation deguisee.
///
/// `docs/RAM-ETAT-DE-LART.md` est la source des affirmations que le produit a le droit de faire sur
/// la memoire vive. Le risque n'est pas qu'on y ecrive une betise : c'est qu'on y ecrive un nombre
/// plausible sans savoir d'ou il vient, puis qu'on l'affiche au client comme un fait mesure.
///
/// La regle du document est donc mecanique, et ce test est ce qui la rend capable d'echouer :
///   • une fourchette qui contient un chiffre EXIGE une URL de source et une date de consultation ;
///   • une fourchette `UNKNOWN` EXIGE la classe `NON CHIFFRE` — on ne laisse pas une ligne se dire
///     sourcee tout en refusant de chiffrer, c'est la position confortable qui n'engage a rien ;
///   • la classe `MESURE ICI` est interdite dans ce fichier : il ne contient que du savoir externe,
///     et rien de ce qu'on lit ailleurs n'a ete mesure sur la machine du client.
///
/// ══ ce qu'il regarde, et c'est la question qui compte ══
/// Il regarde toutes les lignes du tableau AFFIRMATIONS, pas un echantillon, et il exige d'en
/// trouver au moins autant qu'au jour ou il a ete ecrit. Sans ce plancher, vider le tableau serait
/// la maniere la plus simple de passer le controle — le mode de panne que cette boucle a trouve
/// cinq fois : une regle ecrite, appliquee a rien.
/// </summary>
public sealed class TheRamStateOfTheArtHasNoUnsourcedClaimTests
{
    // Une ligne du tableau AFFIRMATIONS : `| R-01 | ... | ... | ... | ... | ... | ... |`.
    private static readonly Regex ClaimRow = new(@"^\|\s*(R-\d+)\s*\|(.+)\|\s*$", RegexOptions.Multiline);
    private static readonly Regex ContainsDigit = new(@"\d");
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$");

    // Le tableau en portait 6 le 06/09/2026. Il peut grandir, jamais retrecir sans qu'on le voie.
    private const int ClaimsAtGateWriting = 6;

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

    private sealed record Claim(string Id, string Range, string Class, string Quality, string Source, string ReadOn);

    private static List<Claim> Claims()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "RAM-ETAT-DE-LART.md"));

        return ClaimRow.Matches(doc).Select(m =>
        {
            // Colonnes apres l'id : affirmation | fourchette | classe | qualite | source | consultee le
            var cells = m.Groups[2].Value.Split('|').Select(c => c.Trim()).ToArray();
            cells.Length.Should().Be(6, "la ligne {0} doit porter exactement 7 colonnes", m.Groups[1].Value);
            return new Claim(m.Groups[1].Value, cells[1], cells[2], cells[3], cells[4], cells[5]);
        }).ToList();
    }

    [Fact]
    public void EveryNumberedClaimCarriesASourceAndTheDateItWasRead()
    {
        var claims = Claims();
        claims.Should().HaveCountGreaterThanOrEqualTo(
            ClaimsAtGateWriting,
            "vider le tableau ne doit jamais etre une facon de passer ce controle");

        foreach (var c in claims.Where(c => ContainsDigit.IsMatch(c.Range)))
        {
            c.Source.Should().Contain("http", "la fourchette de {0} porte un chiffre", c.Id);
            IsoDate.IsMatch(c.ReadOn).Should().BeTrue(
                "{0} doit dire QUAND sa source a ete lue (AAAA-MM-JJ), pas seulement laquelle", c.Id);
        }
    }

    [Fact]
    public void AClaimThatRefusesToQuantifyIsLabelledAsSuch()
    {
        foreach (var c in Claims().Where(c => c.Range == "UNKNOWN"))
            c.Class.Should().Be("NON CHIFFRE", "{0} ne chiffre pas : sa classe doit le dire au client", c.Id);
    }

    [Fact]
    public void NothingInThisFileMayClaimItWasMeasuredOnThisMachine()
    {
        foreach (var c in Claims())
            c.Class.Should().NotBe("MESURE ICI",
                "{0} vient d'une lecture externe : aucune ligne de ce document n'a ete mesuree ici", c.Id);
    }

    // Une ligne du tableau MYTHES : `| M-01 | ... | ... | ... |`.
    private static readonly Regex MythRow = new(@"^\|\s*(M-\d+)\s*\|(.+)\|\s*$", RegexOptions.Multiline);
    private static readonly Regex Backticked = new(@"`([^`]+)`");

    /// <summary>
    /// Un document qui se trompe sur son propre depot est cru par la session suivante.
    ///
    /// Le tableau MYTHES dit, pour chaque manipulation memoire populaire, ce que le produit fait a
    /// la place ET **ou c'est ecrit dans le code**. Cette derniere colonne est la seule verifiable,
    /// et c'est justement celle qui pourrit : un fichier deplace, un levier ajoute, et le document
    /// affirme « aucun code » sur une regle que le produit applique depuis des mois.
    ///
    /// Trouve le 06/09/2026 en ecrivant ce test : la ligne SysMain disait « (aucun code) » alors
    /// que `LeverCatalog.EvaluateSysMain` et `Finding.Memory.SysMainOff` existaient deja.
    ///
    /// La regle : tout ce qui est entre accents graves dans cette colonne doit **exister** --
    /// soit comme fichier, soit comme texte present sous `src/`. Le seul echappatoire est la
    /// mention explicite « aucun code », qui est une position assumee, pas un oubli.
    /// </summary>
    [Fact]
    public void EveryCodePointerInTheMythTableResolves()
    {
        var root = RepoRoot();
        var doc = File.ReadAllText(Path.Combine(root, "docs", "RAM-ETAT-DE-LART.md"));
        var rows = MythRow.Matches(doc);
        rows.Count.Should().BeGreaterThanOrEqualTo(4, "vider le tableau ne passe pas ce controle");

        var sources = FichiersDuDepot.Sous(Path.Combine(root, "src"), "*.*")
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".resx"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        var corpus = string.Join(Environment.NewLine, sources.Select(File.ReadAllText));

        foreach (Match row in rows)
        {
            var cells = row.Groups[2].Value.Split('|').Select(c => c.Trim()).ToArray();
            var where = cells[^1];
            var pointers = Backticked.Matches(where).Select(m => m.Groups[1].Value).ToList();
            pointers.Should().NotBeEmpty(
                "{0} doit nommer soit l'endroit du code, soit l'API qui prouve son absence",
                row.Groups[1].Value);

            // « aucun code » est une AFFIRMATION D'ABSENCE, et une absence ne se prouve pas en la
            // declarant : il lui faut un coupable nomme. La ligne cite donc l'API Win32 par
            // laquelle le mythe s'implementerait, et le test exige qu'elle ne soit nulle part.
            // Le jour ou quelqu'un l'ecrit, ce test rougit -- c'est la lecon des gardes d'absence
            // de LD, appliquee a un document.
            var mustBeAbsent = where.Contains("aucun code");

            foreach (var ptr in pointers)
            {
                var asPath = Path.Combine(root, ptr.Replace('/', Path.DirectorySeparatorChar));
                var present = File.Exists(asPath) || corpus.Contains(ptr);

                if (mustBeAbsent)
                    present.Should().BeFalse(
                        "{0} se dit sans code, mais `{1}` existe sous src/", row.Groups[1].Value, ptr);
                else
                    present.Should().BeTrue(
                        "{0} cite `{1}`, qui n'existe nulle part sous src/", row.Groups[1].Value, ptr);
            }
        }
    }
}
