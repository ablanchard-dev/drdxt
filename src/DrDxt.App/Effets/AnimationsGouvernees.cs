using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Animation;

namespace DrDxt.App.Effets;

/// <summary>
/// Une transition qui répond à un geste du joueur (survol, appui, sélection). Elle peut jouer pendant que DrDXT est au premier
/// plan, puisque le joueur l'a provoquée ; mais quand Windows a coupé ses animations et que le joueur n'a pas choisi d'animer
/// DrDXT quand même (<see cref="MoteurDAnimation.FigeParWindows"/>), elle va droit à sa valeur.
/// </summary>
public sealed class TransitionDeGeste : DoubleAnimation
{
    /// <summary>La règle lue à chaque image ; remplacée dans les tests.</summary>
    internal static Func<bool> Figee { get; set; } = () => MoteurDAnimation.FigeParWindows;

    protected override Freezable CreateInstanceCore() => new TransitionDeGeste();

    protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
        => Valeur(Figee(), To ?? defaultDestinationValue, () => base.GetCurrentValueCore(defaultOriginValue, defaultDestinationValue, animationClock));

    /// <summary>Pur : figée, la valeur d'arrivée ; sinon l'image calculée par l'animation.</summary>
    internal static double Valeur(bool figee, double arrivee, Func<double> image) => figee ? arrivee : image();
}

/// <summary>
/// Un éclat qui joue sans geste, quand le scan rend son résultat. Comme tout ce qui bouge seul, il obéit au moteur
/// (<see cref="MoteurDAnimation.Actif"/>) : fenêtre derrière le jeu, réduite, ou animations coupées, il rend tout de suite sa
/// dernière valeur, celle où la bille se pose. WPF scelle le calcul de <see cref="DoubleAnimationUsingKeyFrames"/> : l'éclat
/// porte donc ses propres images clés, à temps fixes et dans l'ordre, chacune interpolée par elle-même.
/// </summary>
[ContentProperty(nameof(KeyFrames))]
public sealed class EclatSansGeste : DoubleAnimationBase
{
    /// <summary>La règle lue à chaque image ; remplacée dans les tests.</summary>
    internal static Func<bool> Arrete { get; set; } = () => !MoteurDAnimation.Actif;

    public static readonly DependencyProperty KeyFramesProperty =
        DependencyProperty.Register(nameof(KeyFrames), typeof(DoubleKeyFrameCollection), typeof(EclatSansGeste));

    public EclatSansGeste() => SetValue(KeyFramesProperty, new DoubleKeyFrameCollection());

    public DoubleKeyFrameCollection KeyFrames
    {
        get => (DoubleKeyFrameCollection)GetValue(KeyFramesProperty);
        set => SetValue(KeyFramesProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new EclatSansGeste();

    protected override Duration GetNaturalDurationCore(Clock clock)
        => KeyFrames.Count > 0 ? new Duration(KeyFrames[^1].KeyTime.TimeSpan) : Duration.Automatic;

    protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock animationClock)
    {
        if (KeyFrames.Count == 0) return defaultOriginValue;
        if (Arrete()) return KeyFrames[^1].Value;
        var t = animationClock.CurrentTime ?? TimeSpan.Zero;
        var depuis = defaultOriginValue;
        var debut = TimeSpan.Zero;
        foreach (DoubleKeyFrame image in KeyFrames)
        {
            var fin = image.KeyTime.TimeSpan;
            if (t < fin) return image.InterpolateValue(depuis, (t - debut).Ticks / (double)(fin - debut).Ticks);
            (depuis, debut) = (image.Value, fin);
        }
        return depuis;
    }
}
