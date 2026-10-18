using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Les guillemets « » sont français : les textes anglais de l'App et du docteur portent des guillemets droits "".
public class GuillemetsAnglaisTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    [Theory]
    [InlineData("src/DrDxt.Infrastructure/Resources/Strings.resx")]
    [InlineData("src/DrDxt.Core/Resources/Docteur.resx")]
    public void Aucun_texte_anglais_ne_porte_de_guillemets_francais(string fichier)
    {
        var valeurs = XDocument.Load(Path.Combine(Racine(), fichier)).Root!.Elements("data")
            .Select(x => (Cle: x.Attribute("name")!.Value, Texte: x.Element("value")?.Value ?? "")).ToList();

        valeurs.Should().HaveCountGreaterThan(100, "témoin : le fichier lu porte les textes");
        valeurs.Where(v => v.Texte.Contains('«') || v.Texte.Contains('»')).Select(v => v.Cle).Should().BeEmpty();
    }
}
