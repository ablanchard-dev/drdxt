using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DrDxt.App.Effets;

/// <summary>Le shader de la bille (Effets/BilleLiquide.hlsl), exposé à WPF.</summary>
public sealed class BilleLiquideEffect : ShaderEffect
{
    // Gelé : partagé par toutes les instances, lisible depuis tout thread (voir ChromeLiquideEffect).
    private static readonly PixelShader Shader = Geler(new PixelShader
    {
        UriSource = new Uri("pack://application:,,,/DrDxt.App;component/Effets/BilleLiquide.ps"),
    });

    private static PixelShader Geler(PixelShader s)
    {
        if (s.CanFreeze) s.Freeze();
        return s;
    }

    public BilleLiquideEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(StudioProperty);
        var texture = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/DrDxt.App;component/Assets/studio-chrome.png"));
        texture.Freeze();
        var studio = new ImageBrush(texture); studio.Freeze(); Studio = studio;
        UpdateShaderValue(TempsProperty);
        UpdateShaderValue(LuminositeProperty);
        UpdateShaderValue(TeinteProperty);
        UpdateShaderValue(TailleProperty);
    }

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(BilleLiquideEffect), 0);

    public static readonly DependencyProperty StudioProperty = RegisterPixelShaderSamplerProperty(nameof(Studio), typeof(BilleLiquideEffect), 1, SamplingMode.Bilinear);
    public Brush Studio { get => (Brush)GetValue(StudioProperty); set => SetValue(StudioProperty, value); }

    public static readonly DependencyProperty TempsProperty = Registre("Temps", 0.0, 0);
    public static readonly DependencyProperty LuminositeProperty = Registre("Luminosite", 1.0, 1);
    public static readonly DependencyProperty TailleProperty = Registre("Taille", 200.0, 3);

    public static readonly DependencyProperty TeinteProperty = DependencyProperty.Register(
        "Teinte", typeof(Color), typeof(BilleLiquideEffect),
        new UIPropertyMetadata(Color.FromRgb(0x57, 0x5B, 0x61), PixelShaderConstantCallback(2)));

    private static DependencyProperty Registre(string nom, double defaut, int registre) =>
        DependencyProperty.Register(nom, typeof(double), typeof(BilleLiquideEffect),
            new UIPropertyMetadata(defaut, PixelShaderConstantCallback(registre)));

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public double Temps { get => (double)GetValue(TempsProperty); set => SetValue(TempsProperty, value); }
    public double Luminosite { get => (double)GetValue(LuminositeProperty); set => SetValue(LuminositeProperty, value); }
    public double Taille { get => (double)GetValue(TailleProperty); set => SetValue(TailleProperty, value); }
    public Color Teinte { get => (Color)GetValue(TeinteProperty); set => SetValue(TeinteProperty, value); }
}

/// <summary>
/// La bille du bouton central. Le chrome liquide du fond posé
/// sur une sphère qui tourne en 3D, des rubans nets à liseré de lumière, un effet qui évolue sans jamais se répéter
/// (Effets/BilleLiquide.hlsl). Calculée sur la carte graphique, à la taille réelle : nette à toute échelle.
///
/// C'est un indicateur : elle vit même animations Windows coupées, et s'arrête quand la fenêtre passe derrière le jeu.
/// Plus vite quand <see cref="Rapide"/> (le scan travaille).
/// </summary>
public sealed class BilleDeChrome : System.Windows.Controls.Border
{
    private readonly BilleLiquideEffect _effet = new();
    private readonly Stopwatch _chrono = new();
    private double _temps;

    public BilleDeChrome()
    {
        Child = new Rectangle { Fill = Brushes.Black, Effect = _effet };
        IsHitTestVisible = false;
        MoteurDAnimation.HorlogePour(this, Avancer, indicateur: () => true);
        SizeChanged += (_, _) => _effet.Taille = ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    public static readonly DependencyProperty RapideProperty = DependencyProperty.Register(
        nameof(Rapide), typeof(bool), typeof(BilleDeChrome), new PropertyMetadata(false));

    public bool Rapide { get => (bool)GetValue(RapideProperty); set => SetValue(RapideProperty, value); }

    /// <summary>La bille s'éclaire au survol, davantage au maintien. 1 = au repos.</summary>
    public static readonly DependencyProperty LuminositeProperty = DependencyProperty.Register(
        nameof(Luminosite), typeof(double), typeof(BilleDeChrome),
        new PropertyMetadata(1.0, (d, e) => ((BilleDeChrome)d)._effet.Luminosite = (double)e.NewValue));

    public double Luminosite { get => (double)GetValue(LuminositeProperty); set => SetValue(LuminositeProperty, value); }

    private void Avancer()
    {
        if (!_chrono.IsRunning) { _chrono.Start(); return; }
        // Borné : après une pause, le métal reprend où il s'était arrêté au lieu de sauter.
        _temps += Math.Min(_chrono.Elapsed.TotalSeconds, 0.1) * (Rapide ? 2.2 : 0.9);
        _chrono.Restart();
        _effet.Temps = _temps;
    }
}

