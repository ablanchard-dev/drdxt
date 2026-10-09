using System.IO;
using System.Windows.Controls;
using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 29/09/2026 (boucle « vivant et prouvé », B7) : les courbes avant/après surgissaient d'un bloc. Elles se tracent de
// gauche à droite à chaque nouvelle mesure, sous le gouverneur.
public sealed class TraceDesCourbesTests
{
    [Fact]
    public void Une_nouvelle_mesure_trace_les_courbes_quand_le_gouverneur_l_autorise()
    {
        var (masqueAnime, masqueFige) = StaApplication.Run(() =>
        {
            var a = new Canvas();
            TraceDesCourbes.Jouer(a, animer: true);
            var f = new Canvas();
            TraceDesCourbes.Jouer(f, animer: false);
            return (a.OpacityMask is not null, f.OpacityMask is not null);
        });

        masqueAnime.Should().BeTrue("le balayage révèle les courbes");
        masqueFige.Should().BeFalse("témoin : figé, les courbes sont là d'un coup");
    }

    [Fact]
    public void Le_trace_est_branche_sur_les_mesures_du_graphique()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "DashboardView.xaml"));

        xaml.Should().Contain("<Canvas Width=\"1052\" Height=\"136\" effets:TraceDesCourbes.Declencheur=\"{Binding AfterFrameTimes}\"");
    }
}
