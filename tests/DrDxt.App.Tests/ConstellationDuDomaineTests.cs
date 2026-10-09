using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, D4.6) : l'en-tête vivant d'un domaine, un point par lecture. Les points restent dans le cadre,
// ne dépassent jamais le maximum, et ne s'alignent pas en une droite.
public sealed class ConstellationDuDomaineTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(40)]
    public void Les_points_restent_dans_le_cadre_et_sont_bornes(int lectures)
    {
        var points = ConstellationDuDomaine.Points(lectures, 230, 84);
        points.Count.Should().Be(lectures < 3 ? 0 : Math.Min(lectures, 14), "sous trois lectures, aucun point");
        points.Where(p => p.X < 0 || p.X > 230 || p.Y < 0 || p.Y > 84).Should().BeEmpty();
    }

    // 03/10/2026 (D8.1, revue n° 3) : sous trois lectures, le pôle seul ressemblait à un rendu raté ; rien n'est dessiné.
    [Theory]
    [InlineData(2, false)]
    [InlineData(5, true)]
    public void Sous_trois_lectures_rien_n_est_dessine(int lectures, bool dessine)
    {
        var opaques = 0;
        var t = new Thread(() =>
        {
            var c = new ConstellationDuDomaine { Lectures = lectures };
            c.Measure(new System.Windows.Size(190, 44));
            c.Arrange(new System.Windows.Rect(0, 0, 190, 44));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(190, 44, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(c);
            var px = new byte[190 * 44 * 4];
            rtb.CopyPixels(px, 190 * 4, 0);
            for (var i = 3; i < px.Length; i += 4) if (px[i] > 0) opaques++;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        (opaques > 0).Should().Be(dessine, "témoin à 5 lectures : le rendu dessine bien quelque chose");
    }

    [Fact]
    public void Les_points_ne_forment_pas_une_ligne_droite()
    {
        var points = ConstellationDuDomaine.Points(8, 230, 84);
        points.Select(p => Math.Round(p.X)).Distinct().Count().Should().BeGreaterThan(2);
        points.Select(p => Math.Round(p.Y)).Distinct().Count().Should().BeGreaterThan(2);
    }
}
