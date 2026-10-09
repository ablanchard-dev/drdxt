using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>DOC-WIN (09/10/2026) : le mainteneur a refusé DISM /ResetBase (irréversible, aucun gain). Aucune ligne du produit
/// ne le lance ; le retour se voit ici.</summary>
public sealed class JamaisResetBaseTests
{
    [Fact]
    public void Le_produit_ne_lance_jamais_ResetBase()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var fichiers = Directory.EnumerateFiles(Path.Combine(d!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();
        fichiers.Should().HaveCountGreaterThan(300, "témoin : le crible a lu le vrai code");
        fichiers.Where(f => File.ReadAllLines(f).Any(l => !l.TrimStart().StartsWith("//") && l.Contains("/ResetBase")))
            .Select(Path.GetFileName).Should().BeEmpty();
    }
}
