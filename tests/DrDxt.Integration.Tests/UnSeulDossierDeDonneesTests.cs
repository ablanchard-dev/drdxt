using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Douze fichiers recalculaient %LOCALAPPDATA%\DrDXT a la main ; ils passent par DossierDonnees.
// Un treizieme qui le refait echappe a la redirection de developpement : une photo en DEBUG ecrirait alors,
// en silence, dans les donnees du joueur.
public sealed class UnSeulDossierDeDonneesTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    [Fact]
    public void Seul_DossierDonnees_construit_le_dossier_des_donnees()
    {
        var motif = new Regex(@"SpecialFolder\.LocalApplicationData\)\s*,\s*""DrDXT""");
        var sources = FichiersDuDepot.Sous(Path.Combine(Racine().FullName, "src"), "*.cs")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        sources.Should().HaveCountGreaterThan(100, "temoin : le crible lit le code");

        sources.Where(f => motif.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetFileName(f))
            .Should().BeEquivalentTo(new[] { "DossierDonnees.cs" });
    }
}
