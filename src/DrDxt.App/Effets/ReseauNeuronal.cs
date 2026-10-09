using System.Windows;
using System.Windows.Media;

namespace DrDxt.App.Effets;

/// <summary>Ce que le réseau raconte.</summary>
public enum ModeReseau
{
    /// <summary>Avant le scan : le réseau respire, quelques éclats circulent.</summary>
    Repos,

    /// <summary>Pendant le scan : les éclats remontent vers le noyau, une onde balaie le réseau.</summary>
    Scan,

    /// <summary>Après : le réseau se calme.</summary>
    Resultat,
}

/// <summary>Positions sur l'orbite des pôles, partagées par le réseau et par le panneau des libellés
/// pour que chaque libellé tombe exactement sur son pôle.</summary>
public static class Orbite
{
    public static Point Position(int index, int nombre, Size taille, double centreX, double centreY,
        double rayonX, double rayonY, double angleDepartDegres)
    {
        var angle = (angleDepartDegres + 360.0 * index / Math.Max(1, nombre)) * Math.PI / 180.0;
        return new Point(
            taille.Width * centreX + Math.Cos(angle) * taille.Width * rayonX,
            taille.Height * centreY + Math.Sin(angle) * taille.Height * rayonY);
    }

    /// <summary>Une liaison : une courbe de Bézier quadratique ; un rayon va du noyau à un pôle, sinon d'un pôle à son voisin.</summary>
    public readonly record struct Liaison(Point Depart, Point Controle, Point Arrivee, bool EstRayon);

    /// <summary>Le tracé du réseau, partagé par le rendu de la boucle (tools/rendus-3d) et les éclats qui y glissent
    /// (<see cref="ReseauNeuronal"/>) : les rayons d'abord, leur indice est celui du pôle, puis les arcs entre voisins.</summary>
    public static (Point Centre, Point[] Poles, Liaison[] Liaisons) Liaisons(Size taille, int nombre, double rayonX, double rayonY, double angleDepart)
    {
        var centre = new Point(taille.Width / 2, taille.Height / 2);
        nombre = Math.Max(0, nombre);
        var poles = Enumerable.Range(0, nombre).Select(i => Position(i, nombre, taille, 0.5, 0.5, rayonX, rayonY, angleDepart)).ToArray();
        // Les rayons, tous courbés du même côté : ils tournent en spirale douce.
        var liaisons = poles.Select(p =>
        {
            var v = p - centre;
            return new Liaison(centre, new Point((centre.X + p.X) / 2 - v.Y * 0.12, (centre.Y + p.Y) / 2 + v.X * 0.12), p, true);
        }).ToList();
        // Les arcs entre pôles voisins, bombés vers l'extérieur : un anneau autour du noyau.
        if (nombre > 2)
            for (var i = 0; i < nombre; i++)
            {
                var a = poles[i];
                var b = poles[(i + 1) % nombre];
                var milieu = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                liaisons.Add(new Liaison(a, milieu + (milieu - centre) * 0.18, b, false));
            }
        return (centre, poles, liaisons.ToArray());
    }
}

/// <summary>
/// Ce que le scan dit sur le réseau : le noyau au centre, un pôle par composant lu (processeur, carte graphique, mémoire,
/// stockage, Windows) sur une orbite.
///
/// 06/10/2026 (le mainteneur : « fluide chrome, pas un truc 2D ») : les tubes, les billes et le métal qui coule sont en vraie 3D
/// (rendu posé dessous). Ce calque ne garde que ce que dit la lecture : l'onde qui balaie pendant
/// le scan, et l'éclat d'un composant à l'instant où il est lu. La boucle et les billes des composants sont un rendu
/// Cycles posé dessous (Assets/boucle.png). Aucune interaction souris.
///
/// L'horloge ne bat qu'avec le <see cref="MoteurDAnimation"/> : fenêtre en arrière-plan ⇒ rien ne bouge.
/// </summary>
public sealed class ReseauNeuronal : FrameworkElement
{
    /// <summary>Les teintes de la lumière du scan : blanc, glace, acier. Pas de couleur : DrDXT est noir et argent.</summary>
    internal static readonly Color[] Teintes =
    [
        Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0xE4, 0xF2, 0xFF),
        Color.FromRgb(0xC8, 0xD0, 0xDC),
    ];

    private static readonly Brush HaloPoleLu = Geler(new RadialGradientBrush(
        [new GradientStop(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Color.FromArgb(0x00, 0xE4, 0xF2, 0xFF), 1)]));

    private Point _centre;
    private Point[] _poles = [];
    private Size _tailleGeneree;
    private double _temps;
    private double _onde = -1;

    public ReseauNeuronal()
    {
        MoteurDAnimation.HorlogePour(this, Avancer);
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(ModeReseau), typeof(ReseauNeuronal),
        new FrameworkPropertyMetadata(ModeReseau.Repos, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((ReseauNeuronal)d)._onde = 0));

    public static readonly DependencyProperty NombreDePolesProperty = DependencyProperty.Register(
        nameof(NombreDePoles), typeof(int), typeof(ReseauNeuronal),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ReseauNeuronal)d)._tailleGeneree = default));

    public static readonly DependencyProperty RayonXProperty = DependencyProperty.Register(
        nameof(RayonX), typeof(double), typeof(ReseauNeuronal), new FrameworkPropertyMetadata(0.34, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RayonYProperty = DependencyProperty.Register(
        nameof(RayonY), typeof(double), typeof(ReseauNeuronal), new FrameworkPropertyMetadata(0.34, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AngleDepartProperty = DependencyProperty.Register(
        nameof(AngleDepart), typeof(double), typeof(ReseauNeuronal), new FrameworkPropertyMetadata(-90.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>29/09/2026 (B1) : les pôles déjà lus par le scan, un bit par pôle dans l'ordre de l'orbite. Pendant le
    /// scan, un pôle lu s'allume, avec une onde brève à l'instant où il l'est.</summary>
    public static readonly DependencyProperty PolesAllumesProperty = DependencyProperty.Register(
        nameof(PolesAllumes), typeof(int), typeof(ReseauNeuronal),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((ReseauNeuronal)d).NoterAllumages((int)e.OldValue, (int)e.NewValue)));

    public int PolesAllumes { get => (int)GetValue(PolesAllumesProperty); set => SetValue(PolesAllumesProperty, value); }

    private readonly Dictionary<int, double> _allumeA = [];

    private void NoterAllumages(int avant, int apres)
    {
        if (apres == 0) _allumeA.Clear();
        for (var i = 0; i < 31; i++)
            if ((apres & (1 << i)) != 0 && (avant & (1 << i)) == 0) _allumeA[i] = _temps;
    }

    /// <summary>La taille du halo d'un pôle et s'il est vif. Hors scan, la lecture n'y change rien.</summary>
    internal static (double Taille, bool Vif) AspectDuPole(ModeReseau mode, bool allume) => mode switch
    {
        ModeReseau.Scan => allume ? (36, true) : (18, false),
        _ => (22, false),
    };

    public ModeReseau Mode { get => (ModeReseau)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public int NombreDePoles { get => (int)GetValue(NombreDePolesProperty); set => SetValue(NombreDePolesProperty, value); }
    public double RayonX { get => (double)GetValue(RayonXProperty); set => SetValue(RayonXProperty, value); }
    public double RayonY { get => (double)GetValue(RayonYProperty); set => SetValue(RayonYProperty, value); }
    public double AngleDepart { get => (double)GetValue(AngleDepartProperty); set => SetValue(AngleDepartProperty, value); }

    private void Avancer()
    {
        var dt = MoteurDAnimation.Cadence.TotalSeconds;
        _temps += dt;
        if (Mode == ModeReseau.Scan)
        {
            _onde += dt / 1.8;
            if (_onde > 1) _onde = 0;
        }
        else if (_onde >= 0)
        {
            _onde += dt / 1.2;
            if (_onde > 1) _onde = -1;
        }
        if (Mode == ModeReseau.Scan || _onde >= 0) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var taille = new Size(ActualWidth, ActualHeight);
        if (taille.Width < 40 || taille.Height < 40) return;
        if (taille != _tailleGeneree)
        {
            _tailleGeneree = taille;
            (_centre, _poles, _) = Orbite.Liaisons(taille, NombreDePoles, RayonX, RayonY, AngleDepart);
        }
        var scan = Mode == ModeReseau.Scan;

        // L'onde de balayage, du noyau vers l'extérieur.
        if (_onde >= 0)
        {
            var rayonMax = Math.Max(taille.Width, taille.Height) * 0.62;
            var alpha = (byte)(Math.Clamp(1 - _onde, 0, 1) * (scan ? 70 : 35));
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0xE6, 0xEA, 0xF0)), 1.2),
                _centre, rayonMax * _onde, rayonMax * _onde * 0.9);
        }

        // Un composant lu pendant le scan : sa bille s'éclaire, avec une onde de 0,8 s à l'instant de la lecture.
        for (var ordre = 0; ordre < _poles.Length; ordre++)
        {
            var (taillePole, vif) = AspectDuPole(Mode, (PolesAllumes & (1 << ordre)) != 0);
            if (!vif) continue;
            var p = _poles[ordre];
            dc.DrawEllipse(HaloPoleLu, null, p, taillePole, taillePole);
            if (_allumeA.TryGetValue(ordre, out var t0) && _temps - t0 < 0.8)
            {
                var k = (_temps - t0) / 0.8;
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)((1 - k) * 170), 0xFF, 0xFF, 0xFF)), 2), p, 14 + 46 * k, 14 + 46 * k);
            }
        }
    }

    private static T Geler<T>(T objet) where T : Freezable
    {
        objet.Freeze();
        return objet;
    }
}
