using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DrDxt.App.Effets;

public sealed class ChromeSoupleEffect : ShaderEffect
{
    private static readonly PixelShader Shader = Charger();
    private static PixelShader Charger()
    {
        var shader = new PixelShader { UriSource = new Uri("pack://application:,,,/DrDxt.App;component/Effets/ChromeSouple.ps") };
        shader.Freeze(); return shader;
    }
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(nameof(Input), typeof(ChromeSoupleEffect), 0);
    public static readonly DependencyProperty TempsProperty = DependencyProperty.Register(nameof(Temps), typeof(double), typeof(ChromeSoupleEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty,value); }
    public double Temps { get => (double)GetValue(TempsProperty); set => SetValue(TempsProperty,value); }
    public static readonly DependencyProperty TeinteProperty = DependencyProperty.Register(nameof(Teinte), typeof(Color), typeof(ChromeSoupleEffect), new UIPropertyMetadata(Colors.White, PixelShaderConstantCallback(1)));
    public Color Teinte { get => (Color)GetValue(TeinteProperty); set => SetValue(TeinteProperty,value); }
    public static readonly DependencyProperty ActiviteProperty = DependencyProperty.Register(nameof(Activite), typeof(double), typeof(ChromeSoupleEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(2)));
    public double Activite { get => (double)GetValue(ActiviteProperty); set => SetValue(ActiviteProperty,value); }
    public static readonly DependencyProperty SurvolProperty = DependencyProperty.Register(nameof(Survol), typeof(double), typeof(ChromeSoupleEffect), new UIPropertyMetadata(-1.0, PixelShaderConstantCallback(3)));
    public double Survol { get => (double)GetValue(SurvolProperty); set => SetValue(SurvolProperty,value); }
    public static readonly DependencyProperty EclatProperty = DependencyProperty.Register(nameof(Eclat), typeof(double), typeof(ChromeSoupleEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(4)));
    public double Eclat { get => (double)GetValue(EclatProperty); set => SetValue(EclatProperty,value); }
    public static readonly DependencyProperty LusProperty = DependencyProperty.Register(nameof(Lus), typeof(double), typeof(ChromeSoupleEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(5)));
    public double Lus { get => (double)GetValue(LusProperty); set => SetValue(LusProperty,value); }
    public ChromeSoupleEffect(){PixelShader=Shader;UpdateShaderValue(InputProperty);UpdateShaderValue(TempsProperty);UpdateShaderValue(TeinteProperty);UpdateShaderValue(ActiviteProperty);UpdateShaderValue(SurvolProperty);UpdateShaderValue(EclatProperty);UpdateShaderValue(LusProperty);}
}

/// <summary>Huit rubans de chrome dans leur tuyau, un par composant, calculés en continu sur un fond transparent.</summary>
public sealed class BranchesDeChrome : Grid
{
    private readonly ChromeSoupleEffect _effet = new();
    private double _temps, _activite, _eclat;
    private int _vise = -1;

    /// <summary>Les huit pôles, comme la vue les pose (ScanView : RayonX 0,37, RayonY 0,35, départ en haut).</summary>
    public const int Nombre = 8;
    public const double RayonX = .37, RayonY = .35;

    /// <summary>10/10/2026 : la carte sous la souris, ou -1. La carte du haut pend au-dessus de son icône, les autres
    /// en dessous ; la boîte suit la carte (194 de large, ~110 de haut), l'icône étant sur le pôle.</summary>
    public static int CarteSous(Point souris, Size taille)
    {
        for (var i = 0; i < Nombre; i++)
        {
            var pole = Orbite.Position(i, Nombre, taille, .5, .5, RayonX, RayonY, -90);
            var haut = i == 0;
            var boite = new Rect(pole.X - 97, haut ? pole.Y - 110 : pole.Y - 24.5, 194, 134.5);
            if (boite.Contains(souris)) return i;
        }
        return -1;
    }

    /// <summary>Emil Kowalski (animations.dev) : un survol revient des dizaines de fois par jour, il reste court et suit
    /// une courbe qui part vite (ease-out) : ~150 ms pour s'allumer, ~250 ms pour s'éteindre, jamais en boucle.</summary>
    public static double EclatSuivant(double eclat, bool vise, double dt)
        => vise ? eclat + (1 - eclat) * Math.Min(1, dt / .05) : eclat * Math.Max(0, 1 - dt / .08);
    public static readonly DependencyProperty RapideProperty = DependencyProperty.Register(nameof(Rapide), typeof(bool), typeof(BranchesDeChrome), new PropertyMetadata(false));
    public bool Rapide { get => (bool)GetValue(RapideProperty); set => SetValue(RapideProperty,value); }
    /// <summary>10/10/2026 : les domaines lus par le scan (un bit par composant, dans l'ordre du cercle).</summary>
    public static readonly DependencyProperty PolesLusProperty = DependencyProperty.Register(nameof(PolesLus), typeof(int), typeof(BranchesDeChrome),
        new PropertyMetadata(0, (d, e) => ((BranchesDeChrome)d)._effet.Lus = (int)e.NewValue));
    public int PolesLus { get => (int)GetValue(PolesLusProperty); set => SetValue(PolesLusProperty,value); }
    /// <summary>La couleur du métal des rubans ; l'ombre dessous reste noire.</summary>
    public static readonly DependencyProperty TeinteProperty = DependencyProperty.Register(nameof(Teinte), typeof(Color), typeof(BranchesDeChrome),
        new PropertyMetadata(Colors.White, (d, e) => ((BranchesDeChrome)d)._effet.Teinte = (Color)e.NewValue));
    public Color Teinte { get => (Color)GetValue(TeinteProperty); set => SetValue(TeinteProperty,value); }
    public BranchesDeChrome()
    {
        IsHitTestVisible=false;
        Children.Add(new Rectangle { Fill=Brushes.White, Effect=_effet });
        MoteurDAnimation.HorlogePour(this,()=>
        {
            var dt=MoteurDAnimation.Cadence.TotalSeconds;
            _temps+=dt*(Rapide?1.8:1);
            _effet.Temps=_temps;
            // 08/10/2026 : les influx montent en une demi-seconde quand le scan part, s'éteignent en une seconde à la fin.
            _activite=Rapide?Math.Min(1,_activite+dt*2):Math.Max(0,_activite-dt);
            _effet.Activite=_activite;
            var sous=IsVisible?CarteSous(System.Windows.Input.Mouse.GetPosition(this),new Size(ActualWidth,ActualHeight)):-1;
            if(sous>=0&&sous!=_vise){_vise=sous;_effet.Survol=sous;}
            _eclat=EclatSuivant(_eclat,sous>=0&&sous==_vise,dt);
            if(_eclat<.01&&sous<0){_vise=-1;_effet.Survol=-1;}
            _effet.Eclat=_eclat;
        });
    }
}

/// <summary>Un trait d'ancrage ouvert, parcouru par un reflet lent.</summary>
public sealed class AncrageArgente : FrameworkElement
{
    private double _temps;
    public AncrageArgente()
    {
        IsHitTestVisible=false;
        MoteurDAnimation.HorlogePour(this,()=>{_temps+=MoteurDAnimation.Cadence.TotalSeconds;InvalidateVisual();});
    }
    protected override void OnRender(DrawingContext dc)
    {
        var centre=.5+.24*Math.Sin(_temps*.8);
        var brush=new LinearGradientBrush {StartPoint=new Point(0,0),EndPoint=new Point(1,0)};
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0,255,255,255),0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(45,255,255,255),Math.Max(.01,centre-.2)));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(215,255,255,255),centre));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(45,255,255,255),Math.Min(.99,centre+.2)));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0,255,255,255),1));
        brush.Freeze();
        dc.DrawRectangle(brush,null,new Rect(0,ActualHeight/2,ActualWidth,1));
    }
}
