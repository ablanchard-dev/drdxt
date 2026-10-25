using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// La pastille d'état était un point plat sur un halo, puis une perle de chrome, puis un point plat cerclé de chrome : un
// feu tricolore par domaine, sur l'orbite et dans la navigation, que le joueur lisait comme un gabarit tout fait. L'état ne
// garde que son mot, et un domaine sans rien de signalé se tait.
public sealed class PastilleEnPerleTests
{
    [Fact]
    public void L_etat_d_un_domaine_est_son_mot_et_le_silence_dit_que_tout_va_bien()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));

        var gabarit = Regex.Match(xaml, "<DataTemplate x:Key=\"ContenuEtat\">(.*?)</DataTemplate>", RegexOptions.Singleline).Groups[1].Value;
        gabarit.Should().NotBeEmpty("témoin : le gabarit de l'état est lu");
        gabarit.Should().NotContain("<Ellipse", "plus de point de couleur");
        gabarit.Should().Contain("Text=\"{Binding EtatLibelle}\"", "le mot dit l'état");
        gabarit.Should().Contain("EtatDomaine.RienDeSignale").And.Contain("Collapsed");
    }
}
