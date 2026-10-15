using System.IO;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Un lien qui promet une étape du guide arrive sur elle : l'ancre visée existe dans le guide.
public sealed class LienDuGuideTests
{
    [Fact]
    public void L_ancre_de_la_sous_tension_existe_dans_le_guide()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var guide = File.ReadAllText(Path.Combine(d!.FullName, "docs", "A-FAIRE-A-LA-MAIN.md"));
        var ancre = LienDeRapport.GuideSousTension[(LienDeRapport.GuideSousTension.IndexOf('#') + 1)..];
        guide.Should().Contain($"<a id=\"{ancre}\"></a>");
        guide[guide.IndexOf($"<a id=\"{ancre}\"></a>", StringComparison.Ordinal)..].Should().Contain("### 5.23");
    }
}
