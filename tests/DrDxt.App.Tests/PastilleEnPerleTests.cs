using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 29/09/2026 (boucle « vivant et prouvé », B8) : la pastille d'état était un point plat sur un halo. Elle devient une
// perle de chrome teintée de son état : volume (reflet et ombre) et liseré chrome ; le mot reste à côté.
// 03/10/2026 (boucle 2D2P, D5.3, revue) : le reflet brillant et le halo flou faisaient daté (« aqua ») ; la
// pastille devient un point plat de l'état, cerclé d'un liseré chrome fin, sans reflet ni flou. Le mot reste.
public sealed class PastilleEnPerleTests
{
    [Fact]
    public void La_pastille_d_etat_est_un_point_plat_cercle_de_chrome_sans_reflet_ni_flou()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));

        var gabarit = Regex.Match(xaml, "<DataTemplate x:Key=\"ContenuEtat\">(.*?)</DataTemplate>", RegexOptions.Singleline).Groups[1].Value;
        gabarit.Should().NotBeEmpty("témoin : le gabarit de la pastille est lu");
        gabarit.Should().Contain("x:Name=\"Perle\"").And.Contain("LisereChromeBrush");
        gabarit.Should().NotContain("BlurEffect", "pas de halo flou").And.NotContain("RadialGradientBrush", "pas de reflet brillant");
        gabarit.Should().Contain("Text=\"{Binding EtatLibelle}\"", "le mot reste : la couleur seule ne dit rien");
    }
}
