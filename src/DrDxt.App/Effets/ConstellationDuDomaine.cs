using System.Windows;
using System.Windows.Media;

namespace DrDxt.App.Effets;

/// <summary>
/// L'en-tête vivant d'un domaine. Le langage de l'écran d'analyse (un pôle relié à ce
/// qu'il a lu) porté sur chaque page : un pôle à gauche, un fil fin vers un point par lecture, les constats plus vifs.
/// Une impulsion parcourt les fils au rythme du <see cref="MoteurDAnimation"/> ; c'est une décoration, elle se fige quand
/// le jeu est devant. Rien de cliquable.
/// </summary>
public sealed class ConstellationDuDomaine : FrameworkElement
{
    private const int MaximumDePoints = 14;
    private double _phase;

    private static readonly Brush Fil = Geler(new SolidColorBrush(Color.FromArgb(0x55, 0xE6, 0xEA, 0xF0)));
    private static readonly Brush PointLu = Geler(new SolidColorBrush(Color.FromArgb(0xB0, 0xE6, 0xEA, 0xF0)));
    private static readonly Brush PointSignale = Geler(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)));
    private static readonly Brush Impulsion = Geler(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Brush HaloPole = Geler(new RadialGradientBrush(Color.FromArgb(0xCC, 0xE6, 0xEA, 0xF0), Color.FromArgb(0x00, 0xE6, 0xEA, 0xF0)));
    private static readonly Brush CoeurPole = Geler(new SolidColorBrush(Color.FromRgb(0xE8, 0xFE, 0xFF)));

    private static Brush Geler(Brush b) { b.Freeze(); return b; }

    public ConstellationDuDomaine()
    {
        IsHitTestVisible = false;
        MoteurDAnimation.HorlogePour(this, () =>
        {
            _phase = (_phase + MoteurDAnimation.Cadence.TotalSeconds / 2.4) % 1;
            InvalidateVisual();
        });
    }

    public static readonly DependencyProperty LecturesProperty = DependencyProperty.Register(
        nameof(Lectures), typeof(int), typeof(ConstellationDuDomaine), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SignalesProperty = DependencyProperty.Register(
        nameof(Signales), typeof(int), typeof(ConstellationDuDomaine), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Le nombre de lectures du domaine (mesures, états lus, constats).</summary>
    public int Lectures { get => (int)GetValue(LecturesProperty); set => SetValue(LecturesProperty, value); }
    /// <summary>Combien de ces lectures sont des constats : les points les plus vifs.</summary>
    public int Signales { get => (int)GetValue(SignalesProperty); set => SetValue(SignalesProperty, value); }

    /// <summary>La place de chaque point, pure : un éventail à droite du pôle, en deux rangs qui alternent pour ne pas
    /// former une ligne droite. Jamais plus de <see cref="MaximumDePoints"/> ; jamais hors du cadre.</summary>
    public static IReadOnlyList<Point> Points(int lectures, double largeur, double hauteur)
    {
        // Une ou deux lectures donnaient un trait isolé qui ressemblait à un bug ; sous trois, rien.
        var n = lectures < 3 ? 0 : Math.Clamp(lectures, 0, MaximumDePoints);
        var pole = Pole(hauteur);
        var points = new List<Point>(n);
        for (var i = 0; i < n; i++)
        {
            var t = n == 1 ? 0.5 : i / (double)(n - 1);
            var angle = (-62 + 124 * t) * Math.PI / 180;
            var rang = i % 2 == 0 ? 0.62 : 0.94;
            var rayon = (largeur - pole.X - 8) * rang;
            var x = pole.X + Math.Cos(angle) * rayon;
            var y = pole.Y + Math.Sin(angle) * Math.Min(rayon, hauteur / 2 - 6);
            points.Add(new Point(Math.Clamp(x, 4, largeur - 4), Math.Clamp(y, 4, hauteur - 4)));
        }
        return points;
    }

    private static Point Pole(double hauteur) => new(hauteur / 2.6, hauteur / 2);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var pole = Pole(h);
        var points = Points(Lectures, w, h);
        // Un pôle seul, sans fil, passerait pour un rendu raté : mieux vaut ne rien dessiner.
        if (points.Count == 0) return;
        var signales = Math.Min(Signales, points.Count);
        var plume = new Pen(Fil, 1);
        for (var i = 0; i < points.Count; i++)
        {
            dc.DrawLine(plume, pole, points[i]);
            var u = (_phase + i * 0.137) % 1;
            dc.DrawEllipse(Impulsion, null, new Point(pole.X + (points[i].X - pole.X) * u, pole.Y + (points[i].Y - pole.Y) * u), 1.4, 1.4);
        }
        for (var i = 0; i < points.Count; i++)
            dc.DrawEllipse(i < signales ? PointSignale : PointLu, null, points[i], i < signales ? 3.2 : 2.2, i < signales ? 3.2 : 2.2);
        dc.DrawEllipse(HaloPole, null, pole, 16, 16);
        dc.DrawEllipse(CoeurPole, null, pole, 4.5, 4.5);
    }
}
