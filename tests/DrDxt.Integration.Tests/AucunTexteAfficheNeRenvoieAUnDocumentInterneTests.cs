using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Une source que le lecteur ne peut pas ouvrir n'est pas une source.
///
/// Vu sur un scan reel : « Plan north star §2.6 et cahier §3 » sous une recommandation, et
/// « Decision DrDXT BIOS Doctor §2/§5 » sous chaque gain du guide BIOS. Ce sont les noms de nos
/// notes de travail. Pour la personne devant l'ecran, c'est une autorite qu'on lui demande de croire
/// sans pouvoir la verifier — exactement ce que le produit s'interdit.
///
/// Ce gate lit toutes les chaines du catalogue affiche (les deux langues).
/// </summary>
public sealed class AucunTexteAfficheNeRenvoieAUnDocumentInterneTests
{
    private static readonly Regex ReferenceInterne = new(
        @"§|\bcahier\b|north star|revalidation|brief de recherche|BIOS Doctor|\bR\d\b|DECISION-|research/|\bM\d\b",
        RegexOptions.IgnoreCase);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static List<(string Fichier, string Cle, string Valeur)> Chaines()
    {
        var dossier = Path.Combine(RepoRoot(), "src", "DrDxt.Infrastructure", "Resources");
        return Directory.EnumerateFiles(dossier, "Strings*.resx")
            .SelectMany(f => XDocument.Load(f).Root!.Elements("data")
                .Select(d => (Path.GetFileName(f), d.Attribute("name")?.Value ?? "", d.Element("value")?.Value ?? "")))
            .ToList();
    }

    [Fact]
    public void Le_crible_mord_sur_les_deux_phrases_vues_a_l_ecran()
    {
        ReferenceInterne.IsMatch("Plan north star §2.6 et cahier §3").Should().BeTrue();
        ReferenceInterne.IsMatch("Décision DrDXT BIOS Doctor §2/§5 (impact EXPO/XMP).").Should().BeTrue();
        ReferenceInterne.IsMatch("Profil certifié par le fabricant de la mémoire (AMD EXPO, Intel XMP).").Should().BeFalse();
    }

    [Fact]
    public void Aucune_chaine_affichee_ne_cite_un_document_interne()
    {
        var chaines = Chaines();
        chaines.Should().HaveCountGreaterThan(500, "le crible doit avoir lu le catalogue des deux langues");

        chaines.Where(c => ReferenceInterne.IsMatch(c.Valeur))
            .Select(c => $"{c.Fichier} / {c.Cle} : « {ReferenceInterne.Match(c.Valeur).Value} »")
            .Should().BeEmpty("ces textes s'affichent tels quels a quelqu'un qui n'a pas le depot");
    }
}
