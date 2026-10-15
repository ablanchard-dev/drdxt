using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Un anneau fixe autour du bouton central passerait pour une jauge qui ne bouge pas :
// autour de la bille, seule la vraie jauge dessine un cercle.
public sealed class AnneauDuNoyauNEstPasUneJaugeTests
{
    [Fact]
    public void Le_bouton_central_n_a_plus_d_anneau_fixe()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var app = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "App.xaml"));

        app.Should().Contain("<Style TargetType=\"effets:NoyauDeMaintien\">", "témoin : le gabarit du bouton est lu");
        app.Should().NotContain("x:Name=\"AnneauDuNoyau\"");
    }
}
