using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Un bouton qui n'ouvre qu'une boite « bientot disponible » promet ce qui
// n'existe pas. Reglages, Historique, « Plus » et « Voir le rapport » l'etaient ; retires. Ce gate
// empeche le suivant de revenir : aucune vue de l'App ne branche plus un clic sur OnComingSoon.
public sealed class NoComingSoonButtonGateTests
{
    [Fact]
    public void No_view_wires_a_button_to_a_coming_soon_box()
    {
        var vues = FichiersDuDepot.Sous(Path.Combine(RepoRoot(), "src", "DrDxt.App"), "*.xaml").ToList();
        vues.Should().NotBeEmpty("sinon le gate ne regarde rien");

        vues.Where(v => File.ReadAllText(v).Contains("OnComingSoon", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Should().BeEmpty();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }
}
