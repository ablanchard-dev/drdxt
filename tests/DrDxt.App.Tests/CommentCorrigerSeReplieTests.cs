using System.IO;
using System.Xml.Linq;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

// « Comment corriger » dépliée d'office faisait un pavé par constat (texte, commandes icacls). La marche à suivre à la main se
// replie ; ce que DrDXT fait lui-même (un bouton par geste) reste visible.
public sealed class CommentCorrigerSeReplieTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void La_marche_a_suivre_se_replie_et_les_gestes_restent_visibles()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var vue = XDocument.Parse(VueScan.Texte(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"))).Root!;
        var ligne = vue.Descendants().Single(e => (string?)e.Attribute(X + "Key") == "LigneDeCarte");
        ligne.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "DepliantRemede").Attribute("IsChecked").Should().BeNull("replié d'office");

        var repli = ligne.Descendants().Single(e => ((string?)e.Attribute("Visibility"))?.Contains("ElementName=DepliantRemede") == true);
        repli.ToString().Should().Contain("{Binding RemedeLisible}").And.Contain("CommandeACopier").And.Contain("LabelCadreALaMain");
        repli.ToString().Should().NotContain("{Binding Gestes}", "les gestes de DrDXT restent hors du repli");
        ligne.ToString().Should().Contain("{Binding Gestes}");

        // La valeur lue (un chemin) s'ouvre avec le détail, sous le chevron du titre.
        var valeur = ligne.Descendants().Single(e => (string?)e.Attribute("Text") == "{Binding ValeurAAfficher}");
        valeur.Ancestors().Any(e => ((string?)e.Attribute("Visibility") ?? "").Contains("ElementName=Pourquoi")).Should().BeTrue();
    }
}
