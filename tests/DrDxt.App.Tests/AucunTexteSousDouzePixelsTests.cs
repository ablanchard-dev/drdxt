using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Microsoft, « Typography in Windows » : sous 12 px Regular, un texte est illisible
// dans certaines langues. Quatre tailles en dessous ont été trouvées à l'audit (dont le mot d'état des perles, 11,5 px).
public sealed class AucunTexteSousDouzePixelsTests
{
    [Fact]
    public void Aucune_taille_de_texte_sous_douze_pixels_dans_l_interface()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "DrDXT.sln"))) dir = Path.GetDirectoryName(dir)!;
        var fichiers = Directory.GetFiles(Path.Combine(dir, "src", "DrDxt.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        fichiers.Count.Should().BeGreaterThan(2, "témoin : les vues ont bien été lues");

        var tailles = new Regex(@"FontSize""\s*Value=""([0-9.]+)""|FontSize=""([0-9.]+)""");
        var fautes = (from f in fichiers
                      from m in tailles.Matches(File.ReadAllText(f)).Cast<Match>()
                      let v = double.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, CultureInfo.InvariantCulture)
                      where v < 12
                      select $"{Path.GetFileName(f)} : {v}").ToList();

        fautes.Should().BeEmpty();
    }
}
