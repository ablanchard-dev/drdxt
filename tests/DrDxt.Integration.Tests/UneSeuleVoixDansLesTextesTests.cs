using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Le produit parle de lui en « DrDXT » (« DrDXT n'a pas testé ce réglage »), jamais en « nous »
/// (« Ce que nous ne savons pas… notre propre instrument »). Deux voix dans un même écran se lisent
/// comme deux auteurs. Ce crible lit toutes les
/// valeurs du fichier de textes français.
/// </summary>
public sealed class UneSeuleVoixDansLesTextesTests
{
    // Aussi « on » quand le sujet est le produit (« on te le prouve », « ce qu'on n'a pas su lire »),
    // vu sur la photo de l'écran de scan. Le « on » général (« quand on cherche la latence ») reste permis.
    private static readonly Regex Nous = new(
        @"\b(nous|notre|nos)\b|\bon (te|t'|t’|ne te|ne le|n'a|n’a|corrige|mesure|annule)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Fact]
    public void Aucun_texte_affiche_ne_parle_en_nous()
    {
        // Témoins : la forme d'avant est vue, « DrDXT » et « noyau » ne le sont pas.
        Nous.IsMatch("Ce que nous ne savons pas").Should().BeTrue();
        Nous.IsMatch("Notre propre instrument").Should().BeTrue();
        Nous.IsMatch("DrDXT ne sait pas, le noyau non plus").Should().BeFalse();
        Nous.IsMatch("Si ta machine est déjà bonne, on te le prouve aussi").Should().BeTrue();
        Nous.IsMatch("Ce qu'on ne te promet pas : un chiffre").Should().BeTrue();
        Nous.IsMatch("Ce qu'on n'a pas pu lire").Should().BeTrue();
        Nous.IsMatch("seulement quand on cherche la latence la plus basse").Should().BeFalse();

        var fichier = Path.Combine(Racine(), "src", "DrDxt.Infrastructure", "Resources", "Strings.fr.resx");
        var valeurs = XDocument.Load(fichier).Descendants("data")
            .Select(d => (Cle: (string?)d.Attribute("name") ?? "", Texte: (string?)d.Element("value") ?? ""))
            .ToList();
        valeurs.Should().HaveCountGreaterThan(400, "le fichier de textes français en comptait 464 le 25/09/2026");

        valeurs.Where(v => Nous.IsMatch(v.Texte)).Select(v => v.Cle)
            .Should().BeEmpty("le produit parle de lui en « DrDXT », pas en « nous »");
    }
}
