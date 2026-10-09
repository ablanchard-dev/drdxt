using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 04/10/2026 (audit 5.2, photo hors écran) : la carte des parties, avec « Prêt à jouer ? » et l'interrupteur du relevé,
// ne s'affichait qu'après un scan. À l'ouverture, le joueur ne voyait ni ses parties ni la vérification d'avant partie,
// alors que c'est là qu'elle sert. Comme l'historique, elle se montre sans scan.
public sealed class LesPartiesSeVoientSansScanTests
{
    [Fact]
    public void La_carte_des_parties_ne_depend_pas_de_l_etat_du_scan()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));

        var titre = xaml.IndexOf("LabelPartiesTitre", StringComparison.Ordinal);
        titre.Should().BePositive();
        var debutCarte = xaml.LastIndexOf("<Border", titre, StringComparison.Ordinal);
        var balise = xaml[debutCarte..xaml.IndexOf('>', debutCarte)];
        balise.Should().Contain("Dalle").And.NotContain("Visibility");
    }
}
