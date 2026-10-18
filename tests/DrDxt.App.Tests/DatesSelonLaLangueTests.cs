using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Les dates affichées s'écrivaient au format français (« dd/MM ») dans le code : l'interface anglaise montrait 18/10 pour
// « Oct 18 ». Les formats viennent des ressources, une par langue.
public class DatesSelonLaLangueTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    private static string Valeur(string fichier, string cle) => XDocument.Load(Path.Combine(Racine(), fichier)).Root!
        .Elements("data").Single(x => x.Attribute("name")!.Value == cle).Element("value")!.Value;

    [Theory]
    [InlineData("src/DrDxt.Infrastructure/Resources/Strings.resx", "Format.Jour", "MM/dd")]
    [InlineData("src/DrDxt.Infrastructure/Resources/Strings.fr.resx", "Format.Jour", "dd/MM")]
    [InlineData("src/DrDxt.Infrastructure/Resources/Strings.resx", "Format.JourHeure", "MM/dd HH:mm")]
    [InlineData("src/DrDxt.Infrastructure/Resources/Strings.fr.resx", "Format.JourHeure", "dd/MM HH:mm")]
    [InlineData("src/DrDxt.Core/Resources/Docteur.resx", "Format.Date", "MM/dd/yyyy")]
    [InlineData("src/DrDxt.Core/Resources/Docteur.fr.resx", "Format.Date", "dd/MM/yyyy")]
    [InlineData("src/DrDxt.Core/Resources/Docteur.resx", "Format.DateHeure", "MM/dd/yyyy HH:mm")]
    [InlineData("src/DrDxt.Core/Resources/Docteur.fr.resx", "Format.DateHeure", "dd/MM/yyyy HH:mm")]
    public void Chaque_langue_porte_son_format_de_date(string fichier, string cle, string format)
        => Valeur(fichier, cle).Should().Be(format);

    [Fact]
    public void Aucune_date_affichee_n_a_son_format_ecrit_dans_le_code()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(Racine(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();
        var ecrit = new Regex(@"ToString\(""(dd/MM|MM/dd)");

        sources.Should().HaveCountGreaterThan(100, "témoin : le crible lit les sources");
        sources.Where(f => ecrit.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).Should().BeEmpty();
    }
}
