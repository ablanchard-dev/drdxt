using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

// Une commande icacls coupée au milieu de son SID ressemblait à deux
// commandes cassées, et « Copier la commande » était au singulier pour deux. Chaque commande tient sur sa ligne (défilement
// si elle déborde) et le lien dit ce qu'il fait, quel que soit le nombre.
public sealed class CommandesSurUneLigneTests
{
    [Fact]
    public void Les_commandes_ne_se_coupent_pas_et_defilent()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = VueScan.Texte(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var bloc = Regex.Match(xaml, @"<ScrollViewer HorizontalScrollBarVisibility=""Auto""[^>]*>\s*<TextBlock Text=""\{Binding CommandeACopier\}""[^>]*>").Value;
        bloc.Should().NotBeEmpty("le bloc des commandes est dans un défilement horizontal").And.Contain("TextWrapping=\"NoWrap\"");
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Le_lien_de_copie_ne_compte_pas_les_commandes(string langue) =>
        new DrDxt.Infrastructure.Localization.ResxLocalizer().Get("Carte.CopierCommande", new CultureInfo(langue))
            .Should().NotContain("la commande").And.NotContain("the command");
}
