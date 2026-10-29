using System.IO;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

// La carte de la sous-tension a été copiée de celle du test de refroidissement, et son bouton
// « Arrêter » restait lié à la commande de ce test : la recherche ne s'arrêtait plus. Chaque carte arrête ce qu'elle lance.
public sealed class CarteSousTensionXamlTests
{
    [Fact]
    public void Le_bouton_Arreter_de_la_recherche_arrete_la_recherche()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var vue = VueScan.Texte(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var debut = vue.IndexOf("la recherche de sous-tension", StringComparison.Ordinal);
        var fin = vue.IndexOf("le test mémoire sans redémarrer", debut, StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0, "témoin : la carte de la sous-tension est trouvée");
        var carte = vue[debut..fin];
        carte.Should().Contain("ArreterRechercheSousTensionCommand").And.NotContain("ArreterTestRefroidissementCommand");
    }
}
