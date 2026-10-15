using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// « Animations coupées par Windows » était un lien cyan collé sous l'action
// principale et lui faisait concurrence. C'est un bouton discret : gris, cerclé, cyan au survol seulement, et écarté.
public sealed class AnimationsCoupeesEnBoutonDiscretTests
{
    private static string Lire(params string[] chemin)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine([d!.FullName, "src", "DrDxt.App", .. chemin]));
    }

    [Fact]
    public void Le_bouton_animations_coupees_est_discret_et_ecarte()
    {
        var bouton = Regex.Match(Lire("Views", "ScanView.xaml"), "<Button [^>]*LabelAnimerMalgreWindows[^>]*>", RegexOptions.Singleline).Value;
        bouton.Should().NotBeEmpty("témoin : le bouton est trouvé");
        bouton.Should().Contain("Style=\"{StaticResource BoutonDiscret}\"").And.NotContain("StaticResource Lien");

        var style = Regex.Match(Lire("App.xaml"), "<Style x:Key=\"BoutonDiscret\"(.*?)</Style>", RegexOptions.Singleline).Groups[1].Value;
        style.Should().Contain("Value=\"{StaticResource TexteSecondaireBrush}\"", "gris au repos");
        style.Should().Contain("<Setter Property=\"Foreground\" Value=\"{StaticResource IrisCyanBrush}\"/>", "cyan au survol");
        style.IndexOf("IrisCyanBrush", StringComparison.Ordinal).Should().BeGreaterThan(style.IndexOf("IsMouseOver", StringComparison.Ordinal), "le cyan n'arrive qu'au survol");
    }
}
