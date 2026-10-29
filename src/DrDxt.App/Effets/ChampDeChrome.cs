using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DrDxt.App.Effets;

/// <summary>Le shader de chrome liquide (Effets/ChromeLiquide.hlsl), exposé à WPF.</summary>
public sealed class ChromeLiquideEffect : ShaderEffect
{
    // Partagé par toutes les instances : non gelé, il serait lié au thread qui l'a créé, et un autre fil lèverait
    // « Le thread appelant ne peut pas accéder à cet objet » depuis PixelShader.get_ShaderRenderMode. Gelé, un
    // Freezable se lit depuis tout thread.
    private static readonly PixelShader Shader = Geler(new PixelShader
    {
        UriSource = new Uri("pack://application:,,,/DrDxt.App;component/Effets/ChromeLiquide.ps"),
    });

    private static PixelShader Geler(PixelShader s)
    {
        if (s.CanFreeze) s.Freeze();
        return s;
    }

    public ChromeLiquideEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TempsProperty);
        UpdateShaderValue(RapportProperty);
        UpdateShaderValue(AmplitudeProperty);
        UpdateShaderValue(FrequenceProperty);
        UpdateShaderValue(IrisationProperty);
        UpdateShaderValue(LuminositeProperty);
        UpdateShaderValue(TeinteProperty);
    }

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(ChromeLiquideEffect), 0);

    public static readonly DependencyProperty TempsProperty = Registre("Temps", 0.0, 0);
    public static readonly DependencyProperty RapportProperty = Registre("Rapport", 1.0, 1);
    public static readonly DependencyProperty AmplitudeProperty = Registre("Amplitude", 0.3, 2);
    public static readonly DependencyProperty FrequenceProperty = Registre("Frequence", 3.0, 3);
    public static readonly DependencyProperty IrisationProperty = Registre("Irisation", 0.55, 4);
    public static readonly DependencyProperty LuminositeProperty = Registre("Luminosite", 1.0, 5);

    public static readonly DependencyProperty TeinteProperty = DependencyProperty.Register(
        "Teinte", typeof(Color), typeof(ChromeLiquideEffect),
        new UIPropertyMetadata(Color.FromRgb(0x2A, 0x2E, 0x38), PixelShaderConstantCallback(6)));

    private static DependencyProperty Registre(string nom, double defaut, int registre) =>
        DependencyProperty.Register(nom, typeof(double), typeof(ChromeLiquideEffect),
            new UIPropertyMetadata(defaut, PixelShaderConstantCallback(registre)));

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public double Temps { get => (double)GetValue(TempsProperty); set => SetValue(TempsProperty, value); }
    public double Rapport { get => (double)GetValue(RapportProperty); set => SetValue(RapportProperty, value); }
    public double Amplitude { get => (double)GetValue(AmplitudeProperty); set => SetValue(AmplitudeProperty, value); }
    public double Frequence { get => (double)GetValue(FrequenceProperty); set => SetValue(FrequenceProperty, value); }
    public double Irisation { get => (double)GetValue(IrisationProperty); set => SetValue(IrisationProperty, value); }
    public double Luminosite { get => (double)GetValue(LuminositeProperty); set => SetValue(LuminositeProperty, value); }
    public Color Teinte { get => (Color)GetValue(TeinteProperty); set => SetValue(TeinteProperty, value); }
}

/// <summary>
/// Un champ de métal qui coule, rendu par la carte graphique à échelle réduite (<see cref="EchelleDeRendu"/>) puis
/// agrandi : lisse par nature, à moitié d'échelle il coûte quatre fois moins de pixels sans différence visible. Il ne
/// bat qu'au rythme du <see cref="MoteurDAnimation"/>.
/// </summary>
public sealed class ChampDeChrome : Border
{
    private readonly ChromeLiquideEffect _effet = new();
    private readonly Stopwatch _chrono = new();
    private double _tempsAccumule;

    public ChampDeChrome()
    {
        var surface = new Rectangle { Fill = Brushes.Black, Effect = _effet };
        Child = surface;
        ClipToBounds = true;
        IsHitTestVisible = false;
        // Le rendu réduit est agrandi en lissage bilinéaire : sans ça, les crêtes du métal font des marches.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        CacheMode = new BitmapCache(EchelleDeRendu);

        MoteurDAnimation.HorlogePour(this, Avancer, () => Indicateur);
        SizeChanged += (_, _) => _effet.Rapport = ActualHeight > 0 ? ActualWidth / ActualHeight : 1.0;
    }

    /// <summary>La sphère du bouton central est un indicateur (elle dit que le produit vit et travaille),
    /// pas une décoration : elle bouge même quand Windows a coupé ses animations. Le fond reste une décoration.
    /// Lu au chargement : se pose dans le XAML, pas en cours de route.</summary>
    public bool Indicateur { get; set; }

    private void Avancer()
    {
        if (!_chrono.IsRunning) { _chrono.Start(); return; }
        // Borné : après une pause (fenêtre en arrière-plan), le métal reprend où il s'était arrêté
        // au lieu de sauter du temps écoulé.
        _tempsAccumule += Math.Min(_chrono.Elapsed.TotalSeconds, 0.1) * Vitesse;
        _chrono.Restart();
        _effet.Temps = _tempsAccumule + Decalage;
    }

    public static readonly DependencyProperty VitesseProperty = DependencyProperty.Register(
        nameof(Vitesse), typeof(double), typeof(ChampDeChrome), new PropertyMetadata(0.22));

    public static readonly DependencyProperty DecalageProperty = DependencyProperty.Register(
        nameof(Decalage), typeof(double), typeof(ChampDeChrome),
        new PropertyMetadata(0.0, (d, e) => ((ChampDeChrome)d)._effet.Temps = ((ChampDeChrome)d)._tempsAccumule + (double)e.NewValue));

    public static readonly DependencyProperty AmplitudeProperty = DependencyProperty.Register(
        nameof(Amplitude), typeof(double), typeof(ChampDeChrome),
        new PropertyMetadata(0.3, (d, e) => ((ChampDeChrome)d)._effet.Amplitude = (double)e.NewValue));

    public static readonly DependencyProperty FrequenceProperty = DependencyProperty.Register(
        nameof(Frequence), typeof(double), typeof(ChampDeChrome),
        new PropertyMetadata(3.0, (d, e) => ((ChampDeChrome)d)._effet.Frequence = (double)e.NewValue));

    public static readonly DependencyProperty IrisationProperty = DependencyProperty.Register(
        nameof(Irisation), typeof(double), typeof(ChampDeChrome),
        new PropertyMetadata(0.55, (d, e) => ((ChampDeChrome)d)._effet.Irisation = (double)e.NewValue));

    public static readonly DependencyProperty LuminositeProperty = DependencyProperty.Register(
        nameof(Luminosite), typeof(double), typeof(ChampDeChrome),
        new PropertyMetadata(1.0, (d, e) => ((ChampDeChrome)d)._effet.Luminosite = (double)e.NewValue));

    public static readonly DependencyProperty TeinteProperty = DependencyProperty.Register(
        nameof(Teinte), typeof(Color), typeof(ChampDeChrome),
        new PropertyMetadata(Color.FromRgb(0x2A, 0x2E, 0x38), (d, e) => ((ChampDeChrome)d)._effet.Teinte = (Color)e.NewValue));

    public static readonly DependencyProperty EchelleDeRenduProperty = DependencyProperty.Register(
        nameof(EchelleDeRendu), typeof(double), typeof(ChampDeChrome),
        new PropertyMetadata(0.5, (d, e) => ((ChampDeChrome)d).CacheMode = new BitmapCache((double)e.NewValue)));

    /// <summary>Unités de temps du shader par seconde.</summary>
    public double Vitesse { get => (double)GetValue(VitesseProperty); set => SetValue(VitesseProperty, value); }

    /// <summary>Décale le motif : deux champs côte à côte ne coulent pas à l'unisson.</summary>
    public double Decalage { get => (double)GetValue(DecalageProperty); set => SetValue(DecalageProperty, value); }

    public double Amplitude { get => (double)GetValue(AmplitudeProperty); set => SetValue(AmplitudeProperty, value); }
    public double Frequence { get => (double)GetValue(FrequenceProperty); set => SetValue(FrequenceProperty, value); }
    public double Irisation { get => (double)GetValue(IrisationProperty); set => SetValue(IrisationProperty, value); }
    public double Luminosite { get => (double)GetValue(LuminositeProperty); set => SetValue(LuminositeProperty, value); }
    public Color Teinte { get => (Color)GetValue(TeinteProperty); set => SetValue(TeinteProperty, value); }

    /// <summary>Part de la résolution à laquelle le shader est calculé, entre 0,25 et 1.</summary>
    public double EchelleDeRendu { get => (double)GetValue(EchelleDeRenduProperty); set => SetValue(EchelleDeRenduProperty, value); }
}
