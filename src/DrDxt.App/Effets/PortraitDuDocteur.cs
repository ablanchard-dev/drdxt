using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DrDxt.App.Effets;

/// <summary>
/// Le docteur de DrDXT, dans un médaillon : anneau chrome, halo irisé qui bat comme un cœur, et une lueur
/// qui balaie sa visière (le tracé cardiaque de l'illustration), comme un examen qui passe.
///
/// Un seul endroit dans l'application : le bilan de santé, là où DrDXT rend son diagnostic. Tout bouge au
/// rythme du <see cref="MoteurDAnimation"/> ; hors de sa règle, le médaillon est une image fixe.
/// </summary>
public sealed class PortraitDuDocteur : Grid
{
    /// <summary>Un battement toutes les 1,4 s.</summary>
    public const double PeriodeDuBattement = 1.4;

    private const double Taille = 148;

    // La visière dans le portrait recadré (400 × 400) : de 24 % à 76 % en largeur, de 33 % à 51 % en hauteur.
    private static readonly Rect Visiere = new(0.24, 0.33, 0.52, 0.18);

    private readonly Ellipse _halo;
    private readonly GradientStop _lueurDebut = new(Color.FromArgb(0, 0x3C, 0xE6, 0xFF), 0);
    private readonly GradientStop _lueurCentre = new(Color.FromArgb(0xB0, 0xC8, 0xF8, 0xFF), 0);
    private readonly GradientStop _lueurFin = new(Color.FromArgb(0, 0x3C, 0xE6, 0xFF), 0);
    private readonly Stopwatch _chrono = new();
    private double _temps;

    public PortraitDuDocteur()
    {
        Width = Height = Taille;
        IsHitTestVisible = false;

        _halo = new Ellipse { Margin = new Thickness(-26), Opacity = 0.5 };
        _halo.SetResourceReference(Shape.FillProperty, "HaloIrisBrush");
        Children.Add(_halo);

        var portrait = new BitmapImage();
        portrait.BeginInit();
        portrait.UriSource = new Uri("pack://application:,,,/DrDxt.App;component/Assets/docteur-portrait.png");
        portrait.DecodePixelWidth = (int)(Taille * 2);
        portrait.CacheOption = BitmapCacheOption.OnLoad;
        portrait.EndInit();
        portrait.Freeze();

        var disque = new Grid { Clip = new EllipseGeometry(new Point(Taille / 2, Taille / 2), Taille / 2, Taille / 2) };
        disque.Children.Add(new Rectangle { Fill = new ImageBrush(portrait) { Stretch = Stretch.UniformToFill } });
        disque.Children.Add(new Rectangle
        {
            Margin = new Thickness(Visiere.X * Taille, Visiere.Y * Taille, (1 - Visiere.Right) * Taille, (1 - Visiere.Bottom) * Taille),
            RadiusX = 8, RadiusY = 8,
            Fill = new LinearGradientBrush(new GradientStopCollection { _lueurDebut, _lueurCentre, _lueurFin }, new Point(0, 0.5), new Point(1, 0.5)),
        });
        Children.Add(disque);

        var anneau = new Ellipse { StrokeThickness = 2 };
        anneau.SetResourceReference(Shape.StrokeProperty, "LisereChromeBrush");
        Children.Add(anneau);

        Poser(0);
        MoteurDAnimation.HorlogePour(this, Avancer);
    }

    private void Avancer()
    {
        if (!_chrono.IsRunning) { _chrono.Start(); return; }
        _temps += Math.Min(_chrono.Elapsed.TotalSeconds, 0.1);
        _chrono.Restart();
        Poser(_temps);
    }

    private void Poser(double t)
    {
        _halo.Opacity = 0.5 + 0.5 * Battement(t);
        // La lueur traverse la visière en 2,2 s, puis laisse une seconde de répit.
        var x = (t % 3.2) / 2.2 * 1.4 - 0.2;
        _lueurDebut.Offset = x - 0.18;
        _lueurCentre.Offset = x;
        _lueurFin.Offset = x + 0.18;
    }

    /// <summary>L'intensité du halo, entre 0 et 1 : deux pulsations rapprochées (le « poum-poum »),
    /// puis le repos jusqu'au battement suivant.</summary>
    public static double Battement(double t)
    {
        var p = (t % PeriodeDuBattement + PeriodeDuBattement) % PeriodeDuBattement / PeriodeDuBattement;
        static double Pic(double x, double centre, double largeur) => Math.Exp(-Math.Pow((x - centre) / largeur, 2));
        return Math.Clamp(Pic(p, 0.10, 0.045) + 0.75 * Pic(p, 0.30, 0.05), 0, 1);
    }
}
