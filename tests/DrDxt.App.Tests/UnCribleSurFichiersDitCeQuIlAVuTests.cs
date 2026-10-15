using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Un crible qui parcourt des fichiers et conclut « aucune faute »
// dit deux choses tres differentes selon ce qu'il a lu : « j'ai lu la suite, elle
// est propre », ou « je n'ai lu aucun fichier ». La seconde est verte aussi.
//
// C'est le defaut que cette boucle contourne a la main depuis ce matin en
// ajoutant un temoin a chaque garde. `AucunTestNePasseSansRienProuver` refuse
// deja le test sans aucune assertion ; celui-ci vise le cran au-dessus : le test
// qui AFFIRME, mais sur un ensemble qui pourrait etre vide sans qu'on le sache.
//
// Mesure du jour : 24 fichiers combinent un parcours de fichiers et un
// `BeEmpty()`. Trois n'avaient aucun temoin de contenu.
//
// Ce que le crible ne fait pas : accuser tout `BeEmpty()`. Sur 116 fichiers qui
// en portent un, 42 n'ont pas de temoin -- et la plupart n'en ont pas besoin :
// verifier qu'un parseur rend une liste vide sur une entree vide se suffit. Seul
// le couple « je parcours des fichiers » + « je conclus au vide » ment en
// silence. Un crible plus large aurait accuse quarante-deux fois, et aurait ete
// desactive le lendemain.
// -----------------------------------------------------------------------
public sealed class UnCribleSurFichiersDitCeQuIlAVuTests
{
    private static readonly Regex Parcourt = new(@"EnumerateFiles|GetFiles\(");
    private static readonly Regex ConclutAuVide = new(@"Should\(\)\.BeEmpty\(");

    // Une assertion qui exige du contenu : elle rougit si le crible n'a rien lu.
    private static readonly Regex Temoin =
        new(@"Should\(\)\.(NotBeEmpty|HaveCountGreaterThan|HaveCount|Contain|BeGreaterThan)\(");

    // Ancres autrement, et verifies un par un. `TheProductMayNotBuildWhatItNeverCalls`
    // compare sa mesure a une liste GELEE non vide, dans les deux sens : si le crible
    // ne lisait plus rien, son second test rougirait -- toute la liste gelee serait
    // « partie ». Lui demander un temoin de plus serait ceder au crible plutot que de
    // le rendre juste.
    private static readonly string[] AncresParUneListeGelee =
    {
        "TheProductMayNotBuildWhatItNeverCallsTests.cs",
    };

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static IEnumerable<FileInfo> FichiersDeTest()
        => new DirectoryInfo(Path.Combine(Racine().FullName, "tests"))
            .GetFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static List<FileInfo> CriblesSurFichiers()
        => FichiersDeTest()
            .Where(f =>
            {
                var s = File.ReadAllText(f.FullName);
                return Parcourt.IsMatch(s) && ConclutAuVide.IsMatch(s);
            })
            .ToList();

    [Fact]
    public void Le_crible_voit_bien_des_cribles_sur_fichiers()
    {
        // Le temoin de ce fichier-ci : il s'applique a lui-meme.
        CriblesSurFichiers().Should().HaveCountGreaterThan(10);
    }

    [Fact]
    public void Le_motif_de_temoin_reconnait_ce_qu_il_doit_reconnaitre()
    {
        // Temoin inverse. La premiere version de ce motif ignorait `HaveCount(2)` et
        // accusait un fichier sain : mesure avant conclusion, la aussi.
        Temoin.IsMatch("fautes.Should().HaveCount(2);").Should().BeTrue();
        Temoin.IsMatch("vus.Should().NotBeEmpty();").Should().BeTrue();
        Temoin.IsMatch("fautes.Should().BeEmpty();").Should().BeFalse();
    }

    [Fact]
    public void Chaque_crible_sur_fichiers_prouve_qu_il_a_lu_quelque_chose()
    {
        var muets = CriblesSurFichiers()
            .Where(f => !AncresParUneListeGelee.Contains(f.Name))
            .Where(f => !Temoin.IsMatch(File.ReadAllText(f.FullName)))
            .Select(f => f.Name)
            .ToList();

        muets.Should().BeEmpty(
            "ces fichiers parcourent la suite et concluent « aucune faute » sans jamais " +
            "affirmer avoir lu quelque chose. Un chemin qui change, un filtre trop strict, " +
            "et ils restent verts sur zero fichier. Ajoutez un temoin de contenu");
    }
}
