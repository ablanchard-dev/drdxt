using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// 27/09/2026 : trois classes (App, Detection, Reconciler.Adapters) declaraient chacune PowerGetActiveScheme
// et liberaient le tampon a leur facon. Trois copies du meme appel natif, c'est trois endroits a corriger le
// jour ou il le faut, et un seul qu'on corrige. Une seule declaration, les autres lui deleguent.
public sealed class UnSeulLecteurDuPlanActifTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    [Fact]
    public void Seul_PlanActif_declare_PowerGetActiveScheme()
    {
        var declaration = new Regex(@"extern\s+uint\s+PowerGetActiveScheme\s*\(");
        var sources = FichiersDuDepot.Sous(Path.Combine(Racine().FullName, "src"), "*.cs")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        sources.Should().HaveCountGreaterThan(100, "temoin : le crible lit le code");

        sources.Where(f => declaration.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Should().BeEquivalentTo(new[] { "PlanActif.cs" });
    }
}
