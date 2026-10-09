using System.Text.RegularExpressions;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Les arguments du service sont ecrits deux fois, dans deux langages.
///
/// En C# (<see cref="PresentMonNaming.ServiceArguments"/>, lu par le helper elevé) et en ISPP dans
/// `installer/DrDXT.iss`, qui est la voie que le TESTEUR emprunte. Une garantie tenue par le code
/// mais trahie par l'installeur n'est pas une garantie.
///
/// Les deux valent **vide**, et c'est une mesure. Le 07/09/2026, elles ont porte quelques
/// heures `--control-pipe \\.\pipe\drdxt-presentmon-control`, pour donner a DrDXT un tube prive et
/// cohabiter avec le service Intel. Trois mesures par le SCM l'ont demoli : le serveur accepte
/// l'option et publie le nom par defaut quand meme — en 2.4.1 comme avec le binaire Intel 2.5.1.
/// Voir `docs/PRESENTMON-COEXISTENCE.md`.
///
/// Mesuré avec ISCC le meme jour : le preprocesseur d'Inno **consomme un antislash sur deux**
/// dans les chaines, guillemets simples comme doubles. `"\\.\pipe\x"` rend `\.\pipe\x`. Tout
/// argument futur portant un chemin devra donc doubler ses antislash — ce test applique la regle,
/// pour qu'elle soit deja en place le jour ou un argument revient.
/// </summary>
public sealed class PresentMonNamingContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }

    private static string Iss() => File.ReadAllText(Path.Combine(RepoRoot(), "installer", "DrDXT.iss"));

    /// <summary>La valeur du `#define PmServiceArgs`, telle qu'ISPP la produira.</summary>
    private static string ArgumentsDeLInstalleur()
    {
        var m = Regex.Match(Iss(), "^#define\\s+PmServiceArgs\\s+\"(?<v>[^\"]*)\"\\s*$", RegexOptions.Multiline);
        m.Success.Should().BeTrue("l'installeur doit definir PmServiceArgs sur une seule ligne");

        // La regle mesuree : ISPP consomme un antislash sur deux.
        return m.Groups["v"].Value.Replace(@"\\", @"\");
    }

    [Fact]
    public void L_installeur_pose_EXACTEMENT_les_arguments_que_le_produit_declare()
    {
        ArgumentsDeLInstalleur().Should().Be(PresentMonNaming.ServiceArguments);
    }

    [Fact]
    public void L_installeur_ne_pose_AUCUN_argument_dont_l_effet_n_a_pas_ete_mesure()
    {
        // La garde qui empeche `--control-pipe` de revenir par distraction : il a ete essaye,
        // et mesure sans effet. Le remettre reintroduirait une difference imaginaire entre deux
        // installations, et une explication fausse du conflit avec le service Intel.
        //
        // On ne regarde que ce qui s'execute -- le #define et les lignes `sc create`. Le fichier
        // explique en commentaire pourquoi l'option a ete abandonnee, et un commentaire n'installe
        // rien. Meme precaution que `PresentMonServiceStartupTests` avec « start= auto ».
        var executables = Iss().Split('\n')
            .Where(l => !l.TrimStart().StartsWith(";", StringComparison.Ordinal)
                     && !l.Contains("{ ---", StringComparison.Ordinal))
            .Where(l => l.Contains("#define PmServiceArgs", StringComparison.Ordinal)
                     || (l.Contains("create", StringComparison.OrdinalIgnoreCase)
                         && l.Contains("binPath", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        executables.Should().NotBeEmpty("le #define et la creation du service doivent exister");
        executables.Should().OnlyContain(l => !l.Contains("--control-pipe", StringComparison.Ordinal));
        executables.Should().OnlyContain(l => !l.Contains("--intro-nsm", StringComparison.Ordinal));
    }
}
