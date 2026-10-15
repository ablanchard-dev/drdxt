using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Une machine sans point à corriger garde Optimiser et « Prêt à jouer ? » : seule la liste des points se cache quand elle
// est vide. Dans le même bloc caché, les deux boutons disparaissaient après un scan propre.
public sealed class OptimiserResteVisibleTests
{
    [Fact]
    public void Seule_la_liste_se_cache_quand_elle_est_vide()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var vue = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var debut = vue.IndexOf("x:Name=\"ListeDesPointsAFaire\"", StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0, "témoin : le bloc de la liste est trouvé");
        var bloc = vue[debut..vue.IndexOf("x:Name=\"ApercuOptimiser\"", debut, StringComparison.Ordinal)];
        bloc.Should().Contain("PointsAFaire.Count", "c'est ce bloc qui se cache quand la liste est vide");
        bloc.Should().NotContain("BoutonOptimiser").And.NotContain("VerifierPretAJouerCommand");
    }
}
