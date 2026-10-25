using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le titre français porte l'accent du « à » : « DirectX 9 à 11 ».
public class TitreUltraLowLatencyTests
{
    [Fact]
    public void Le_titre_francais_dit_DirectX_9_a_11_avec_l_accent()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var resx = XDocument.Load(Path.Combine(d!.FullName, "src", "DrDxt.Infrastructure", "Resources", "Strings.fr.resx"));

        resx.Root!.Elements("data").Single(x => x.Attribute("name")!.Value == "tweak.latency.reflex.title").Element("value")!.Value
            .Should().Contain("DirectX 9 à 11");
    }
}
