using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Sur le bilan, la puce d'un constat « à faire » était cyan sous un état
// orange. Elle prend la couleur de sa gravité, comme la ligne du même constat dans son domaine.
public sealed class PuceDeConstatALaCouleurDeSaGraviteTests
{
    [Fact]
    public void La_puce_d_un_point_a_faire_suit_sa_gravite()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));

        var gabarit = Regex.Match(xaml, @"<DataTemplate DataType=""\{x:Type vm:PointAFaire\}"">(.*?)</DataTemplate>", RegexOptions.Singleline).Groups[1].Value;
        gabarit.Should().NotBeEmpty("témoin : le gabarit du point à faire est lu");
        var puce = Regex.Match(gabarit, "<Ellipse[^>]*>").Value;
        puce.Should().Contain("Entree.Gravite").And.Contain("SeverityColor").And.NotContain("IrisCyanBrush");
    }

    // Dans un domaine, la puce d'une ligne reçoit la ligne entière (gravité ET proposition), pour que
    // le convertisseur rende l'ambre d'un constat d'information et le cyan d'un réglage proposé.
    [Fact]
    public void La_puce_d_une_ligne_de_domaine_recoit_la_ligne_entiere()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var gabarit = Regex.Match(xaml, "<DataTemplate x:Key=\"LigneDeCarte\"(.*?)</DataTemplate>", RegexOptions.Singleline).Groups[1].Value;
        gabarit.Should().NotBeEmpty("témoin : le gabarit est lu");
        Regex.Match(gabarit, "<Ellipse[^>]*>", RegexOptions.Singleline).Value
            .Should().Contain("Fill=\"{Binding Converter={StaticResource SeverityColor}}\"");
    }

    // Deux Run séparés par un retour à la ligne ajoutent déjà une espace ; un format
    // qui commençait aussi par une espace donnait « texte  · Domaine ».
    [Fact]
    public void Le_domaine_d_un_point_a_faire_n_a_qu_une_espace_devant_lui()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        xaml.Should().Contain("StringFormat='· {0}'").And.NotContain("StringFormat=' · {0}'");
    }
}
