using System.IO;
using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>28/09/2026 : le docteur revient, à un seul endroit (le bilan de santé, là où DrDXT rend son
/// diagnostic), dans un médaillon dont le halo bat comme un cœur et dont la visière est balayée par une lueur.</summary>
public sealed class PortraitDuDocteurTests
{
    [Fact]
    public void Le_halo_bat_deux_fois_puis_se_repose_et_recommence()
    {
        var periode = PortraitDuDocteur.PeriodeDuBattement;
        var valeurs = Enumerable.Range(0, 400).Select(i => PortraitDuDocteur.Battement(i * periode / 400)).ToList();

        valeurs.Should().OnlyContain(v => v >= 0 && v <= 1);
        var pics = Enumerable.Range(1, valeurs.Count - 2).Count(i => valeurs[i] > 0.5 && valeurs[i] >= valeurs[i - 1] && valeurs[i] > valeurs[i + 1]);
        pics.Should().Be(2, "le « poum-poum » d'un cœur");
        valeurs.Skip(valeurs.Count * 3 / 5).Should().OnlyContain(v => v < 0.1, "puis le repos");
        PortraitDuDocteur.Battement(periode * 3.25).Should().BeApproximately(PortraitDuDocteur.Battement(periode * 0.25), 1e-9);
    }

    // 03/10/2026 (D4.3) : ma décision, « l'endroit avec le bonhomme violet faut changer ». Le bilan de santé porte la
    // sphère de marque (le logo, la même matière que le bouton central) ; aucune vue n'affiche plus le docteur.
    [Fact]
    public void Le_bilan_porte_la_sphere_de_marque_et_plus_aucune_vue_n_affiche_le_docteur()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var app = Path.Combine(d!.FullName, "src", "DrDxt.App");

        var vues = Directory.GetFiles(Path.Combine(app, "Views"), "*.xaml").Select(File.ReadAllText).ToList();
        vues.Should().NotBeEmpty("témoin : les vues ont été lues");
        vues.Should().NotContain(v => v.Contains("<effets:PortraitDuDocteur"));
        var scan = File.ReadAllText(Path.Combine(app, "Views", "ScanView.xaml"));
        var bilan = scan.IndexOf("LE BILAN DE SANTÉ", StringComparison.Ordinal);
        var rapport = scan.IndexOf("LE RAPPORT", StringComparison.Ordinal);
        scan.IndexOf("{StaticResource SphereDeMarque}", StringComparison.Ordinal).Should().BeInRange(bilan, rapport, "dans la section du bilan de santé");
    }
}
