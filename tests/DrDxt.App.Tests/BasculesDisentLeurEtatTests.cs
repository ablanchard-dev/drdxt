using System.Globalization;
using System.IO;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

// « Relever mes parties » (les FPS) et « Surveiller mes parties » (le ping) sont deux fonctions.
// Chacune est un vrai interrupteur : la piste dit l'état, son mot (« Allumé » / « Éteint ») est à côté,
// et le libellé ne dit que ce que ça fait. La règle : chaque bascule dit son état.
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
        var xaml = VueScan.Texte(Path.Combine(CheminDuDepot(), "src", "DrDxt.App", "Views", "ScanView.xaml"));
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
