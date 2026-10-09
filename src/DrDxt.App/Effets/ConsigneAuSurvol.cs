using System.Windows;
using System.Windows.Media;

namespace DrDxt.App.Effets;

/// <summary>
/// 03/10/2026 (boucle 2D2P, D5.6, revue) : « Maintiens pour …, ou Entrée » était écrit sous chaque action, six fois
/// par écran. Le geste s'apprend sur le rond de l'accueil ; dans les cartes « Agir », la consigne ne paraît qu'au survol
/// ou au focus clavier de sa ligne. Elle garde sa place (opacité, pas de repli) : rien ne saute quand la souris passe.
/// </summary>
public static class ConsigneAuSurvol
{
    public static readonly DependencyProperty ActifProperty = DependencyProperty.RegisterAttached(
        "Actif", typeof(bool), typeof(ConsigneAuSurvol), new PropertyMetadata(false, SurActif));

    public static bool GetActif(DependencyObject d) => (bool)d.GetValue(ActifProperty);
    public static void SetActif(DependencyObject d, bool valeur) => d.SetValue(ActifProperty, valeur);

    /// <summary>La règle, pure : visible quand la ligne est survolée ou contient le focus clavier.</summary>
    public static double Opacite(bool survolee, bool focusDedans) => survolee || focusDedans ? 1.0 : 0.0;

    private static void SurActif(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement consigne || e.NewValue is not true) return;
        consigne.Opacity = 0;
        consigne.Loaded += (_, _) =>
        {
            // La ligne de l'action : la grille qui porte la capsule et son texte.
            if (VisualTreeHelper.GetParent(consigne) is not FrameworkElement colonne
                || VisualTreeHelper.GetParent(colonne) is not FrameworkElement ligne) return;
            void Suivre() => consigne.Opacity = Opacite(ligne.IsMouseOver, ligne.IsKeyboardFocusWithin);
            ligne.MouseEnter += (_, _) => Suivre();
            ligne.MouseLeave += (_, _) => Suivre();
            ligne.IsKeyboardFocusWithinChanged += (_, _) => Suivre();
            Suivre();
        };
    }
}
