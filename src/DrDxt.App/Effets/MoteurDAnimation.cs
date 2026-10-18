using System.Windows;
using System.Windows.Threading;

namespace DrDxt.App.Effets;

/// <summary>
/// Quand l'interface a le droit de bouger.
///
/// DrDXT tourne à côté d'un jeu et mesure ses images par seconde. Une interface qui s'anime pendant
/// cette mesure vole du processeur et de la carte graphique au jeu, et fausse le chiffre qu'elle
/// affiche ensuite. La règle est donc stricte et vit à un seul endroit :
///   - la fenêtre doit être au premier plan (pendant une mesure valable, c'est le jeu qui l'est) ;
///   - elle ne doit pas être réduite ;
///   - Windows ne doit pas avoir coupé les animations (Paramètres, Accessibilité, Effets visuels).
/// Hors de ces trois conditions, tout effet se fige sur sa dernière image et ne coûte plus rien.
/// </summary>
public static class MoteurDAnimation
{
    private static Window? _fenetre;

#if DEBUG
    /// <summary>Photographier le scanner vivant dans une fenêtre hors écran, jamais au premier plan
    /// (l'argument « hors-ecran ») : sans ceci, la règle le fige, à raison. N'existe pas en Release.</summary>
    internal static bool ForcePourCapture;
#endif

    /// <summary>Levé à chaque changement de <see cref="Actif"/>.</summary>
    public static event EventHandler? Change;

    /// <summary>Vrai quand les effets peuvent tourner.</summary>
    public static bool Actif { get; private set; }

    /// <summary>La règle, pure : se teste sans fenêtre. Le joueur peut animer DrDXT même quand
    /// Windows a coupé ses animations (réglage d'optimisation courant, vu sur ma machine : tout était figé) ; le
    /// premier plan et la fenêtre réduite restent fermés, ce sont eux qui protègent la mesure en jeu.</summary>
    public static bool Decider(bool fenetreAuPremierPlan, bool fenetreReduite, bool animationsDeWindows, bool animerMalgreWindows = false) =>
        fenetreAuPremierPlan && !fenetreReduite && (animationsDeWindows || animerMalgreWindows);

    /// <summary>Vrai quand un indicateur de travail en cours peut bouger. Windows avait coupé ses
    /// animations sur ma machine, l'anneau du scan restait figé et l'analyse « faisait buguée ». Un indicateur n'est pas
    /// une décoration : il dit que le produit travaille. Il ignore donc le réglage de Windows, mais garde les deux
    /// portes qui protègent la mesure en jeu (premier plan, fenêtre non réduite).</summary>
    public static bool ActifPourIndicateur { get; private set; }

    /// <summary>Le choix du joueur, gardé d'un lancement à l'autre. Absent ou abîmé : on suit Windows.</summary>
    public static bool AnimerMalgreWindows { get; private set; }

    /// <summary>Vrai quand Windows fige DrDXT et que le joueur n'a pas choisi de l'animer quand même.</summary>
    public static bool FigeParWindows => !SystemParameters.ClientAreaAnimation && !AnimerMalgreWindows;

    private static readonly DrDxt.Core.FichierJsonAtomique Choix =
        new(DrDxt.Core.FichierJsonAtomique.CheminLocal("animations.json"), "ANIMATIONS_CHOIX");

    public static void ChoisirAnimerMalgreWindows(bool animer)
    {
        AnimerMalgreWindows = animer;
        Choix.WriteAtomic(animer ? "true" : "false");
        Reevaluer();
    }

    /// <summary>La cadence des effets : 30 images par seconde suffisent à du métal qui coule, et
    /// laissent le reste au jeu et au compositeur.</summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromMilliseconds(1000.0 / 30.0);

    /// <summary>Branche la règle sur la fenêtre principale. Appelé une fois au démarrage.</summary>
    public static void Brancher(Window fenetre)
    {
        _fenetre = fenetre;
        var lu = Choix.Read();
        AnimerMalgreWindows = lu.IsOk && lu.Value?.Trim() == "true";
        fenetre.Activated += (_, _) => Reevaluer();
        fenetre.Deactivated += (_, _) => Reevaluer();
        fenetre.StateChanged += (_, _) => Reevaluer();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Reevaluer();
        };
        Reevaluer();
    }

    private static void Reevaluer()
    {
        var f = _fenetre;
        var actif = f is not null && Decider(f.IsActive, f.WindowState == WindowState.Minimized,
            SystemParameters.ClientAreaAnimation, AnimerMalgreWindows);
#if DEBUG
        actif |= f is not null && ForcePourCapture;
#endif
        var indicateur = f is not null && Decider(f.IsActive, f.WindowState == WindowState.Minimized, true);
#if DEBUG
        indicateur |= f is not null && ForcePourCapture;
#endif
        if (actif == Actif && indicateur == ActifPourIndicateur) return;
        Actif = actif;
        ActifPourIndicateur = indicateur;
        Change?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Une horloge qui ne bat que quand l'effet est visible et que la règle l'autorise.
    /// Un effet invisible (autre écran de l'application, section repliée) ne coûte rien non plus.</summary>
    public static DispatcherTimer HorlogePour(FrameworkElement element, Action tic, Func<bool>? indicateur = null)
    {
        var horloge = new DispatcherTimer(DispatcherPriority.Render) { Interval = Cadence };
        horloge.Tick += (_, _) => tic();

        void Suivre()
        {
            var doitBattre = (indicateur?.Invoke() == true ? ActifPourIndicateur : Actif) && element.IsVisible && element.IsLoaded;
            if (doitBattre && !horloge.IsEnabled) horloge.Start();
            else if (!doitBattre && horloge.IsEnabled) horloge.Stop();
        }

        EventHandler surChange = (_, _) => Suivre();
        // WPF peut lever Loaded deux fois sans Unloaded entre les deux : l'abonnement n'est pris qu'une fois.
        element.Loaded += (_, _) => { Change -= surChange; Change += surChange; Suivre(); };
        element.Unloaded += (_, _) => { Change -= surChange; horloge.Stop(); };
        element.IsVisibleChanged += (_, _) => Suivre();
        return horloge;
    }
}
