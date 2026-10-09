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
    public ChromeSoupleEffect(){PixelShader=Shader;UpdateShaderValue(InputProperty);UpdateShaderValue(TempsProperty);UpdateShaderValue(TeinteProperty);UpdateShaderValue(ActiviteProperty);}
}

/// <summary>Cinq rubans de chrome dans leur tuyau, calculés en continu sur un fond transparent.</summary>
public sealed class BranchesDeChrome : Grid
{
    private readonly ChromeSoupleEffect _effet = new();
    private double _temps, _activite;
    public static readonly DependencyProperty RapideProperty = DependencyProperty.Register(nameof(Rapide), typeof(bool), typeof(BranchesDeChrome), new PropertyMetadata(false));
    public bool Rapide { get => (bool)GetValue(RapideProperty); set => SetValue(RapideProperty,value); }
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
