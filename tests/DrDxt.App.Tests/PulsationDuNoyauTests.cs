using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>29/09/2026 : « pendant le scan il n'y a rien, pas d'animation » (retour sur l'app). Le noyau au
/// travail respire et lance une onde ; la pulsation passe par le moteur d'animation, pas par un storyboard sans
/// fin qui tournerait encore derrière le jeu.</summary>
public sealed class PulsationDuNoyauTests
{
    [Fact]
    public void La_respiration_reste_entre_zero_et_un_et_monte_au_quart_de_periode()
    {
        NoyauDeMaintien.Battement(0).Pouls.Should().BeApproximately(0.5, 1e-9);
        NoyauDeMaintien.Battement(NoyauDeMaintien.PeriodeDuPouls / 4).Pouls.Should().BeApproximately(1, 1e-9);
        for (var t = 0.0; t < 5; t += 0.037)
        {
            var (pouls, onde) = NoyauDeMaintien.Battement(t);
            pouls.Should().BeInRange(0, 1);
            onde.Should().BeGreaterThanOrEqualTo(0).And.BeLessThan(1);
        }
    }

    [Fact]
    public void Aucune_capsule_ne_pulse_par_un_storyboard_sans_fin()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "App.xaml"));
        var debut = xaml.IndexOf("x:Key=\"CapsuleDeMaintien\"", StringComparison.Ordinal);
        var fin = xaml.IndexOf("</Style>", debut, StringComparison.Ordinal);

        xaml[debut..fin].Should().NotContain("RepeatBehavior=\"Forever\"", "hors du moteur, rien ne s'arrête quand le jeu passe devant");
    }
}
