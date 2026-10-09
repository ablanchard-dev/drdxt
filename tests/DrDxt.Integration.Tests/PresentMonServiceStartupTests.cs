using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Le service de mesure PresentMon doit etre enregistre en DEMANDE (start= demand, soit
/// « Manuel »), jamais en AUTOMATIQUE.
///
/// Pourquoi c'est une garantie et pas une preference : un service de mesure lance a chaque
/// demarrage est exactement le « bolt-on always-on » que le produit refuse -- DrDXT mesure
/// quand on le lui demande, il ne s'installe pas en resident dans la machine du client.
/// Un PC de testeur doit rester un PC naturel.
///
/// CONSTATE le 27/08/2026 : sur la machine de reference, le service tournait en
/// **Automatic** alors que le code et l'installeur posent tous deux `start= demand`. La
/// derive venait d'une installation manuelle, pas du produit -- mais rien ne verrouillait
/// la garantie cote source. Ce fichier est ce verrou : il rougit si un jour quelqu'un
/// enregistre ce service en automatique.
///
/// (Le service deja INSTALLE sur une machine donnee ne peut pas etre corrige par un test :
/// c'est un geste eleve, il vit dans GATES.md.)
/// </summary>
public sealed class PresentMonServiceStartupTests
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

        throw new InvalidOperationException(
            "Could not locate DrDXT.sln by walking up from " + AppContext.BaseDirectory);
    }

    private static string Read(string relative)
    {
        var full = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).Should().BeTrue($"{relative} must exist for this guarantee to mean anything");
        return File.ReadAllText(full);
    }

    /// <summary>Le helper elevie enregistre le service : la commande sc.exe doit porter
    /// `start= demand`. C'est la voie utilisee quand l'app installe le service elle-meme.</summary>
    [Fact]
    public void HelperRegistersPresentMonOnDemand_NeverAutomatic()
    {
        var source = Read("src/DrDxt.ElevatedHelper/Execution/Win32ServiceActions.cs");

        var create = Regex.Match(source, @"create\s+\{?PresentMonServiceName\}?[^""]*|create[^""\n]*PresentMon[^""\n]*");
        source.Should().Contain("start= demand",
            "le service de mesure s'enregistre en DEMANDE -- un service toujours actif est le " +
            "bolt-on que le produit refuse");

        // La ligne qui cree le service ne doit pas demander un demarrage automatique.
        var creationLines = source.Split('\n').Where(l => l.Contains("create") && l.Contains("binPath"));
        creationLines.Should().NotBeEmpty("la commande de creation du service doit exister");
        creationLines.Should().OnlyContain(l => !Regex.IsMatch(l, @"start=\s*auto", RegexOptions.IgnoreCase),
            "aucune creation de service ne doit demander un demarrage automatique");
    }

    /// <summary>L'installeur pose le meme contrat. Une garantie tenue par le code mais trahie
    /// par l'installeur ne serait pas une garantie : c'est l'installeur que le testeur lance.</summary>
    [Fact]
    public void InstallerRegistersPresentMonOnDemand_NeverAutomatic()
    {
        var iss = Read("installer/DrDXT.iss");

        iss.Should().Contain("start= demand",
            "l'installeur enregistre le service de mesure en DEMANDE");

        // On ne regarde que les lignes qui appellent reellement sc create : le fichier
        // contient un commentaire mentionnant « start= auto » pour un service FUTUR
        // (le reconciliateur de la version vendue), et un commentaire n'installe rien.
        var scCreateLines = iss.Split('\n')
            .Where(l => l.Contains("create", StringComparison.OrdinalIgnoreCase)
                     && l.Contains("binPath", StringComparison.OrdinalIgnoreCase))
            .ToList();

        scCreateLines.Should().NotBeEmpty("l'installeur doit contenir la commande de creation du service");
        scCreateLines.Should().OnlyContain(l => !Regex.IsMatch(l, @"start=\s*auto", RegexOptions.IgnoreCase),
            "aucune ligne d'installation ne doit enregistrer un service en automatique");
    }
}
