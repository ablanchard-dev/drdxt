using System.Globalization;
using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, D11.3, revue n° 5) : « Relever mes parties » et « Surveiller mes parties » se lisaient
// comme une seule fonction (ce sont deux : les FPS, le ping) et aucune ne disait son état.
// 08/10/2026 (critique UI, défaut 10) : l'état s'écrivait dans le texte d'un bouton pilule (« Éteint · surveiller mon
// ping »). C'est maintenant un vrai interrupteur : la piste dit l'état, son mot (« Allumé » / « Éteint ») est à côté,
// et le libellé ne dit plus que ce que ça fait. La règle reste : chaque bascule dit son état.
public sealed class BasculesDisentLeurEtatTests
{
    private static string L(string cle) => new DrDxt.Infrastructure.Localization.ResxLocalizer().Get(cle, new CultureInfo("fr"));

    [Theory]
    [InlineData("Parties.Libelle")]
    [InlineData("Ping.Veille.Libelle")]
    [InlineData("Voisins.Calmer.Libelle")]
    public void Le_libelle_ne_recopie_plus_l_etat(string cle) =>
        L(cle).Should().NotContain("·").And.NotStartWith("Éteint").And.NotStartWith("Allumé");

    [Fact]
    public void L_interrupteur_a_ses_deux_mots_d_etat()
    {
        L("Bascule.Allume").Should().Be("Allumé");
        L("Bascule.Eteint").Should().Be("Éteint");
    }

    [Fact]
    public void Les_trois_bascules_sont_de_vrais_interrupteurs_lies_a_leur_etat()
    {
        var xaml = File.ReadAllText(Path.Combine(CheminDuDepot(), "src", "DrDxt.App", "Views", "ScanView.xaml"));
        foreach (var etat in new[] { "CalmerVoisinsActif", "VeillePingActive", "ReleveDesPartiesActif" })
            xaml.Should().Contain($"IsChecked=\"{{Binding {etat}, Mode=OneWay}}\"", etat);
        System.Text.RegularExpressions.Regex.Matches(xaml, "Style=\"\\{StaticResource Bascule\\}\"").Count.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Les_deux_releves_en_partie_se_distinguent() =>
        L("Parties.Libelle").Should().Contain("FPS").And.NotBe(L("Ping.Veille.Libelle"));

    private static string CheminDuDepot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("racine du dépôt introuvable");
    }
}
