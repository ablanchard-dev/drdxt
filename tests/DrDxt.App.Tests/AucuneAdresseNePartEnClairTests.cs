using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 18/09/2026 — « forcer le HTTPS ». DrDXT contacte trois choses : le service de
// licence (api.keygen.sh), un flux de mises a jour quand l'hebergeur en
// configure un, et la page de paiement ouverte dans le navigateur.
//
// Chacune de ces trois adresses a deja son test. C'est exactement le motif qui
// laisse passer la quatrieme : trois verifications nominatives ne disent rien
// de l'URL qu'on ajoutera demain dans un fichier qui n'a pas de test a lui.
//
// Mesure du jour, sans defaut trouve : aucune adresse en clair dans le livre.
// La discipline tenait a la main. Ce crible la rend structurelle -- il ne
// connait aucune des trois adresses, il refuse le SCHEMA.
//
// Une URL en clair n'est pas qu'une note de securite abstraite : sur une cle de
// licence, c'est un secret qui traverse le reseau lisible par qui partage le
// wifi ; sur un flux de mises a jour, c'est quelqu'un d'autre qui choisit ce que
// le produit telecharge, sur une machine ou il s'execute en administrateur.
//
// Ce que le crible ne refuse pas : les espaces de noms XML. `xmlns=
// "http://schemas.microsoft.com/winfx/2006/xaml"` est un identifiant, pas une
// adresse -- rien n'est jamais telecharge depuis la. Le temoin inverse plus bas
// tient cette distinction, sinon quelqu'un desactiverait le crible pour du bruit.
// -----------------------------------------------------------------------
public sealed class AucuneAdresseNePartEnClairTests
{
    private static readonly Regex EnClair = new(@"http://[^\s""'<>]+");

    // Un identifiant d'espace de noms, jamais telecharge. Les trois formes
    // presentes dans les XAML et les csproj du depot.
    private static readonly Regex IdentifiantXml =
        new(@"xmlns(:\w+)?\s*=|schemas\.microsoft\.com|schemas\.openxmlformats\.org|www\.w3\.org|purl\.org");

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    /// <summary>Tout ce qui est LIVRE : le code et les vues, pas les artefacts de build.</summary>
    private static IEnumerable<FileInfo> Livre()
        => new DirectoryInfo(Path.Combine(Racine().FullName, "src"))
            .EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(f => f.Extension is ".cs" or ".xaml" or ".json" or ".config")
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Les adresses en clair d'un fichier, identifiants XML exclus.</summary>
    private static IEnumerable<string> AdressesEnClair(string chemin)
        => File.ReadLines(chemin)
            .Where(l => !IdentifiantXml.IsMatch(l))
            .SelectMany(l => EnClair.Matches(l).Select(m => m.Value));

    [Fact]
    public void Le_crible_lit_bien_le_code_livre()
    {
        // Temoin de contenu : sans lui, un chemin change rendrait le garde vert
        // sur zero fichier, pour toujours.
        var livre = Livre().ToList();

        livre.Should().HaveCountGreaterThan(50);
        livre.Should().Contain(f => f.Name == "App.xaml");
    }

    [Fact]
    public void Le_crible_distingue_une_adresse_d_un_identifiant_xml()
    {
        // Temoin inverse. Un crible qui accuse les xmlns crie au loup a chaque
        // fichier XAML : il serait desactive la semaine suivante.
        EnClair.IsMatch(@"var url = ""http://maj.exemple.fr/flux.json"";").Should().BeTrue();
        IdentifiantXml.IsMatch(@"xmlns=""http://schemas.microsoft.com/winfx/2006/xaml""")
            .Should().BeTrue("un espace de noms XML n'est jamais telecharge");
        EnClair.IsMatch(@"var url = ""https://api.keygen.sh/v1"";").Should().BeFalse();
    }

    [Fact]
    public void Aucune_adresse_contactee_par_le_produit_ne_part_en_clair()
    {
        var nues = Livre()
            .SelectMany(f => AdressesEnClair(f.FullName).Select(a => $"{f.Name} : {a}"))
            .ToList();

        nues.Should().BeEmpty(
            "en clair, une cle de licence est lisible par qui partage le wifi, et c'est " +
            "quelqu'un d'autre qui choisit ce que le produit telecharge -- sur une machine " +
            "ou il s'execute en administrateur");
    }
}
