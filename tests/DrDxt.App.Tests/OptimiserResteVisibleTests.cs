using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Une machine sans point à corriger garde Optimiser et « Prêt à jouer ? » : seule la liste des points se cache quand elle
// est vide. Dans le même bloc caché, les deux boutons disparaissaient après un scan propre. La liste se replie aussi :
// l'accueil ouvre sur Optimiser, pas sur l'inventaire des défauts.
public sealed class OptimiserResteVisibleTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Vue()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return XDocument.Load(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml")).Root!;
    }

    private static XElement Nomme(XElement vue, string nom)
        => vue.Descendants().Single(e => (string?)e.Attribute(X + "Name") == nom);

    [Fact]
    public void Seule_la_liste_se_cache_quand_elle_est_vide()
    {
        var bloc = Nomme(Vue(), "ListeDesPointsAFaire");

        bloc.ToString().Should().Contain("PointsAFaire.Count", "c'est ce bloc qui se cache quand la liste est vide");
        bloc.Descendants().Should().NotContain(e => (string?)e.Attribute(X + "Name") == "BoutonOptimiser");
        bloc.ToString().Should().NotContain("VerifierPretAJouerCommand");
    }

    [Fact]
    public void La_liste_des_defauts_vient_apres_Optimiser_et_se_replie()
    {
        var vue = Vue();
        var ordre = vue.Descendants().Select(e => (string?)e.Attribute(X + "Name")).Where(n => n is not null).ToList();

        ordre.IndexOf("BoutonOptimiser").Should().BeLessThan(ordre.IndexOf("ListeDesPointsAFaire"));
        var depliant = Nomme(vue, "DepliantAFaire");
        depliant.Attribute("IsChecked").Should().BeNull("repliée d'office");
        ((string?)Nomme(vue, "ListeAFaire").Attribute("Visibility")).Should().Contain("ElementName=DepliantAFaire");
    }
}
