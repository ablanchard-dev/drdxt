using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, D9.1) : l'anneau cyan fixe du bouton central se lisait comme une jauge qui ne bouge pas.
// 06/10/2026 (le mainteneur : « le trait est trop épais autour, enlève-le, fais une vraie bille ») : il n'y a plus d'anneau fixe ;
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
