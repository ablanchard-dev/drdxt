using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, D2.6) : sous chaque ligne lue « pour information », « Pourquoi ? » prenait une ligne entière ;
// un « ? » en fin de ligne ouvre la même suite. D5.5 (revue) : deux mécanismes pour la même chose, le « ? » rond
// devient le seul signe partout (mesures, constats, lectures impossibles, processeur bridé), nom complet gardé.
public sealed class PourquoiDesMesuresEnFinDeLigneTests
{
    private static string Vue()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "DrDXT.sln"))) dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine(dir, "src", "DrDxt.App", "Views", "ScanView.xaml"));
    }

    [Fact]
    public void Le_pourquoi_d_une_mesure_est_un_signe_en_fin_de_ligne()
    {
        var vue = Vue();
        var i = vue.IndexOf("x:Name=\"PourquoiMesure\"", StringComparison.Ordinal);
        i.Should().BeGreaterThan(0, "témoin : le bouton existe");
        var bouton = vue[i..vue.IndexOf("/>", i, StringComparison.Ordinal)];
        bouton.Should().Contain("Grid.Row=\"0\"").And.Contain("Grid.Column=\"2\"", "sur la ligne, pas en dessous");
    }

    [Theory]
    [InlineData("PourquoiMesure")]
    [InlineData("Pourquoi")]
    [InlineData("PourquoiNonLu")]
    [InlineData("PourquoiBride")]
    [InlineData("PourquoiPing")]
    [InlineData("PourquoiVeille")]
    [InlineData("PourquoiCalmer")]
    public void Chaque_pourquoi_est_le_meme_signe(string nom)
    {
        var vue = Vue();
        var m = Regex.Match(vue, $"<ToggleButton x:Name=\"{nom}\"[^>]*>");
        m.Success.Should().BeTrue($"témoin : {nom} existe");
        m.Value.Should().Contain("Style=\"{StaticResource BoutonPourquoi}\"");
    }

    // 03/10/2026 (D8.7, revue n° 3) : le « ? » était au bout de la ligne pour les mesures, en dessous ailleurs.
    // Il est au bout de la ligne qu'il explique, partout.
    [Theory]
    [InlineData("Pourquoi")]
    [InlineData("PourquoiNonLu")]
    [InlineData("PourquoiBride")]
    [InlineData("PourquoiPing")]
    [InlineData("PourquoiVeille")]
    [InlineData("PourquoiCalmer")]
    public void Chaque_pourquoi_est_au_bout_de_sa_ligne(string nom)
    {
        var m = Regex.Match(Vue(), $"<ToggleButton x:Name=\"{nom}\"[^>]*>");
        m.Success.Should().BeTrue($"témoin : {nom} existe");
        m.Value.Should().Contain("DockPanel.Dock=\"Right\"", "au bout de la ligne, pas en dessous");
    }

    [Fact]
    public void Voir_le_rapport_n_est_pas_un_bouton_etire()
    {
        var vue = Vue();
        var i = vue.IndexOf("Command=\"{Binding ExportReportCommand}\"", StringComparison.Ordinal);
        i.Should().BeGreaterThan(0, "témoin : le bouton existe");
        var debut = vue.LastIndexOf("<Button", i, StringComparison.Ordinal);
        vue[debut..i].Should().NotContain("Stretch", "un bouton étiré faisait un troisième gabarit");
    }

    [Fact]
    public void Le_signe_garde_son_nom_complet_pour_le_lecteur_d_ecran()
    {
        var vue = Vue();
        var debut = vue.IndexOf("<Style x:Key=\"BoutonPourquoi\"", StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0);
        var style = vue[debut..vue.IndexOf("</Style>", debut, StringComparison.Ordinal)];
        style.Should().Contain("AutomationProperties.Name").And.Contain("Text=\"?\"");
    }

    // 03/10/2026 (D10.4, revue n° 4) : protection du ping indisponible = un seul « ? » dans le bloc (celui de la
    // veille) ; la description de la protection et son « ? » sont dans un conteneur qui ne s'affiche que si elle est disponible.
    [Fact]
    public void La_description_de_la_protection_se_tait_tant_qu_elle_est_indisponible()
    {
        var vue = Vue();
        var i = vue.IndexOf("x:Name=\"PourquoiPing\"", StringComparison.Ordinal);
        i.Should().BeGreaterThan(0, "témoin");
        var conteneur = vue.LastIndexOf("<StackPanel", i, StringComparison.Ordinal);
        vue[conteneur..i].Should().Contain("Visibility=\"{Binding PlafondEnvoiDisponible, Converter={StaticResource BoolVis}}\"");
    }
}
