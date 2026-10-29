using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

// Sur l'accueil, un point à faire n'a plus de puce de couleur : une puce par ligne, plus celles des domaines, faisaient un
// écran pointillé. La gravité se dit par le groupe replié qui les porte et par son mot. Dans un domaine, la puce d'une ligne
// suit encore la gravité de la ligne entière.
public sealed class PuceDeConstatALaCouleurDeSaGraviteTests
{
    [Fact]
    public void Un_point_a_faire_n_a_ni_puce_ni_separateur()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = VueScan.Texte(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));

        var gabarit = Regex.Match(xaml, @"<DataTemplate DataType=""\{x:Type vm:PointAFaire\}"">(.*?)</DataTemplate>", RegexOptions.Singleline).Groups[1].Value;
        gabarit.Should().NotBeEmpty("témoin : le gabarit du point à faire est lu");
        gabarit.Should().NotContain("<Ellipse").And.NotContain("·");
        gabarit.Should().Contain("Entree.Titre").And.Contain("Domaine.Nom", "le domaine reste lisible, en info-bulle");
    }

    // Dans un domaine non plus, pas de puce par ligne : le titre du groupe porte le mot, sans couleur d'état, et reçoit la
    // ligne entière (gravité ET proposition) pour dire « à regarder » d'un constat d'information et « proposé » d'un réglage.
    [Fact]
    public void Le_titre_du_groupe_porte_le_mot_et_recoit_la_ligne_entiere()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = VueScan.Texte(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var gabarit = Regex.Match(xaml, "<DataTemplate x:Key=\"LigneDeCarte\"(.*?)</DataTemplate>", RegexOptions.Singleline).Groups[1].Value;
        gabarit.Should().NotBeEmpty("témoin : le gabarit est lu");
        gabarit.Should().NotContain("<Ellipse").And.NotContain("SeverityColor");
        var groupe = Regex.Match(xaml, "<DataTemplate x:Key=\"GroupeDeConstats\"(.*?)</DataTemplate>", RegexOptions.Singleline).Groups[1].Value;
        groupe.Should().Contain("Text=\"{Binding Premiere, Converter={StaticResource SeverityLabel}, ConverterParameter=Titre}\"")
            .And.Contain("Foreground=\"{StaticResource TexteSecondaireBrush}\"", "le mot seul, sans couleur d'état");
    }
}
