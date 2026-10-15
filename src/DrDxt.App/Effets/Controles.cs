using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DrDxt.App.Effets;

/// <summary>
/// Le noyau : on ne clique pas pour lancer un scan, on maintient.
///
/// Maintenir la souris remplit l'anneau ; relâcher avant la fin le vide. Le geste dit ce que le scan
/// est : un examen qui prend du temps, lancé exprès. Au clavier, Espace ou Entrée lancent tout de
/// suite : le maintien est une signature, jamais un obstacle d'accessibilité.
/// </summary>
public sealed class NoyauDeMaintien : ButtonBase
{
    private readonly DispatcherTimer _horloge = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private DateTime _dernierTic;
    private bool _maintenu;

    public NoyauDeMaintien()
    {
        _horloge.Tick += (_, _) => Avancer();
        Focusable = true;
        MoteurDAnimation.HorlogePour(this, Respirer, indicateur: () => true);
    }

    // Au travail, le noyau respire et lance une onde (« pendant le scan il n'y a rien », retour sur
    // l'app). Par le moteur d'animation : dès que le jeu passe devant, plus rien ne bat.
    public const double PeriodeDuPouls = 1.2;
    private const double PeriodeDeLOnde = 1.6;
    private double _tempsDuPouls;

    /// <summary>La respiration (0 à 1, 0,5 au départ) et la phase de l'onde (0 à 1) à l'instant t.</summary>
    public static (double Pouls, double Onde) Battement(double t) =>
        (0.5 + 0.5 * Math.Sin(2 * Math.PI * t / PeriodeDuPouls), t / PeriodeDeLOnde - Math.Floor(t / PeriodeDeLOnde));

    private void Respirer()
    {
        if (!Occupe)
        {
            if (Pouls != 0 || OndeOpacite != 0) { Pouls = 0; OndeOpacite = 0; OndeEchelle = 1; _tempsDuPouls = 0; }
            return;
        }
        _tempsDuPouls += MoteurDAnimation.Cadence.TotalSeconds;
        var (pouls, onde) = Battement(_tempsDuPouls);
        Pouls = pouls;
        OndeOpacite = 0.8 * (1 - onde);
        OndeEchelle = 1 + 0.22 * onde;
    }

    public static readonly DependencyProperty PoulsProperty = DependencyProperty.Register(
        nameof(Pouls), typeof(double), typeof(NoyauDeMaintien), new PropertyMetadata(0.0));
    public static readonly DependencyProperty OndeOpaciteProperty = DependencyProperty.Register(
        nameof(OndeOpacite), typeof(double), typeof(NoyauDeMaintien), new PropertyMetadata(0.0));
    public static readonly DependencyProperty OndeEchelleProperty = DependencyProperty.Register(
        nameof(OndeEchelle), typeof(double), typeof(NoyauDeMaintien), new PropertyMetadata(1.0));

    /// <summary>La respiration du noyau au travail, entre 0 et 1 ; 0 au repos.</summary>
    public double Pouls { get => (double)GetValue(PoulsProperty); private set => SetValue(PoulsProperty, value); }
    public double OndeOpacite { get => (double)GetValue(OndeOpaciteProperty); private set => SetValue(OndeOpaciteProperty, value); }
    public double OndeEchelle { get => (double)GetValue(OndeEchelleProperty); private set => SetValue(OndeEchelleProperty, value); }

    /// <summary>L'échelle de l'onde était liée dans le modèle, sur une ScaleTransform, par
    /// TemplatedParent. Une transformation n'a pas d'élément gouvernant : quand l'écran n'était pas encore mis en
    /// page, la liaison échouait (« Cannot find governing FrameworkElement »). Le contrôle pose lui-même la
    /// transformation, liée à sa propre propriété par une source explicite.</summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("Onde") is UIElement onde)
        {
            var echelle = new ScaleTransform();
            BindingOperations.SetBinding(echelle, ScaleTransform.ScaleXProperty, new Binding(nameof(OndeEchelle)) { Source = this });
            BindingOperations.SetBinding(echelle, ScaleTransform.ScaleYProperty, new Binding(nameof(OndeEchelle)) { Source = this });
            onde.RenderTransform = echelle;
        }
        // Le remplissage de la capsule suit la progression du maintien, même défaut, même remède.
        if (GetTemplateChild("Remplissage") is UIElement remplissage)
        {
            var largeur = new ScaleTransform();
            BindingOperations.SetBinding(largeur, ScaleTransform.ScaleXProperty, new Binding(nameof(Progression)) { Source = this });
            remplissage.RenderTransform = largeur;
        }
    }

    public static readonly DependencyProperty ProgressionProperty = DependencyProperty.Register(
        nameof(Progression), typeof(double), typeof(NoyauDeMaintien), new PropertyMetadata(0.0));

    public static readonly DependencyProperty DureeMaintienProperty = DependencyProperty.Register(
        nameof(DureeMaintien), typeof(TimeSpan), typeof(NoyauDeMaintien), new PropertyMetadata(TimeSpan.FromMilliseconds(700)));

    public static readonly DependencyProperty EnMaintienProperty = DependencyProperty.Register(
        nameof(EnMaintien), typeof(bool), typeof(NoyauDeMaintien), new PropertyMetadata(false));

    /// <summary>
    /// Le noyau travaille, il ne s'éteint pas.
    ///
    /// Pendant le scan, le noyau est désactivé (on ne relance pas un scan en cours) et le style
    /// le grisait à 50 % comme n'importe quel bouton hors service. Mesuré en photographiant la
    /// fenêtre : au moment précis où la personne vient d'appuyer, la sphère, son chrome et son
    /// anneau passent tous à demi-teinte ; le seul signe de vie restant est un arc fin, lui aussi
    /// à demi-teinte. Le geste répond donc par une extinction, et le scan durant moins d'une
    /// seconde, on ne voit que ça : « j'appuie, ça s'éteint, il ne se passe rien ».
    ///
    /// Occupé distingue les deux raisons de ne plus être cliquable : hors service (on grise) et
    /// au travail (on reste allumé, et on s'agite). Une seule propriété, parce que le style ne
    /// peut pas deviner la raison depuis IsEnabled seul.
    /// </summary>
    public static readonly DependencyProperty OccupeProperty = DependencyProperty.Register(
        nameof(Occupe), typeof(bool), typeof(NoyauDeMaintien), new PropertyMetadata(false,
            // Annexe de B5 : le travail vient de finir (vrai puis faux), la capsule le dit.
            (d, e) => { if (e.OldValue is true && e.NewValue is false) ((NoyauDeMaintien)d).SignalerFait(MoteurDAnimation.Actif); }));

    /// <summary>Annexe de B5 : la coche « Fait » du gabarit apparaît en 120 ms, reste 2 s, s'efface en 200 ms ; rien quand le
    /// gouverneur fige tout (le résultat s'écrit de toute façon à côté de la capsule).</summary>
    internal void SignalerFait(bool animer)
    {
        if (!animer || GetTemplateChild("Fait") is not UIElement coche) return;
        var montre = new DoubleAnimationUsingKeyFrames { Duration = CocheEntree + CocheTenue + CocheSortie };
        montre.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(CocheEntree)));
        montre.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(CocheEntree + CocheTenue)));
        montre.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(CocheEntree + CocheTenue + CocheSortie)));
        coche.BeginAnimation(UIElement.OpacityProperty, montre);
    }

    /// <summary>La coche « Fait » : apparition, temps affiché, effacement.</summary>
    internal static readonly TimeSpan CocheEntree = TimeSpan.FromMilliseconds(120);
    internal static readonly TimeSpan CocheTenue = TimeSpan.FromMilliseconds(2000);
    internal static readonly TimeSpan CocheSortie = TimeSpan.FromMilliseconds(200);

    /// <summary>Vrai quand le travail que ce noyau a lancé est en cours.</summary>
    public bool Occupe { get => (bool)GetValue(OccupeProperty); set => SetValue(OccupeProperty, value); }

    /// <summary>Entre 0 et 1 : la part du maintien accomplie.</summary>
    public double Progression { get => (double)GetValue(ProgressionProperty); set => SetValue(ProgressionProperty, value); }

    public TimeSpan DureeMaintien { get => (TimeSpan)GetValue(DureeMaintienProperty); set => SetValue(DureeMaintienProperty, value); }

    public bool EnMaintien { get => (bool)GetValue(EnMaintienProperty); private set => SetValue(EnMaintienProperty, value); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsEnabled) return;
        Focus();
        CaptureMouse();
        _maintenu = true;
        EnMaintien = true;
        _dernierTic = DateTime.UtcNow;
        _horloge.Start();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Relacher();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        Relacher();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && IsEnabled)
        {
            e.Handled = true;
            OnClick();
            return;
        }
        base.OnKeyDown(e);
    }

    private void Relacher()
    {
        if (!_maintenu) return;
        _maintenu = false;
        EnMaintien = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _dernierTic = DateTime.UtcNow;
        _horloge.Start();
    }

    private void Avancer()
    {
        var maintenant = DateTime.UtcNow;
        var dt = (maintenant - _dernierTic).TotalMilliseconds;
        _dernierTic = maintenant;
        var pas = dt / Math.Max(1, DureeMaintien.TotalMilliseconds);

        if (_maintenu)
        {
            Progression = Math.Min(1, Progression + pas);
            if (Progression >= 1)
            {
                _maintenu = false;
                EnMaintien = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
                OnClick();
            }
        }
        else
        {
            // Retour trois fois plus rapide que l'aller : relâcher doit se sentir tout de suite.
            Progression = Math.Max(0, Progression - pas * 3);
            if (Progression <= 0) _horloge.Stop();
        }
    }

    internal void Declencher() => OnClick();

    /// <summary>L'action part, l'éclair le dit tout de suite, au maintien comme au clavier.</summary>
    protected override void OnClick()
    {
        Eclairer(MoteurDAnimation.Actif);
        base.OnClick();
    }

    /// <summary>Un éclair irisé de 600 ms sur l'élément « Eclair » du gabarit ; rien quand le gouverneur fige tout.</summary>
    internal void Eclairer(bool animer)
    {
        if (!animer || GetTemplateChild("Eclair") is not UIElement eclair) return;
        var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(600) };
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        flash.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        eclair.BeginAnimation(UIElement.OpacityProperty, flash);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Pair(this);

    /// <summary>Pour les lecteurs d'écran, le noyau est un bouton qu'on invoque.</summary>
    private sealed class Pair(NoyauDeMaintien noyau) : ButtonBaseAutomationPeer(noyau), System.Windows.Automation.Provider.IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => nameof(NoyauDeMaintien);
        public override object GetPattern(PatternInterface motif) => motif == PatternInterface.Invoke ? this : base.GetPattern(motif);
        void System.Windows.Automation.Provider.IInvokeProvider.Invoke()
        {
            if (IsEnabled()) ((NoyauDeMaintien)Owner).Declencher();
        }
    }
}

/// <summary>Un anneau qui se remplit, ou qui tourne quand la durée est inconnue.</summary>
public sealed class ArcDeProgression : FrameworkElement
{
    private double _rotation;
    private double _affiche;

    public ArcDeProgression()
    {
        MoteurDAnimation.HorlogePour(this, () =>
        {
            if (Tourne)
            {
                _rotation = (_rotation + 7) % 360;
                InvalidateVisual();
                return;
            }
            var suivant = Suivant(_affiche, Valeur, Plafond, MoteurDAnimation.Cadence.TotalSeconds, Vitesse);
            if (Math.Abs(suivant - _affiche) < 1e-5) return;
            _affiche = suivant;
            InvalidateVisual();
        }, indicateur: () => true);
    }

    /// <summary>La jauge sautait d'un huitième à chaque étape puis restait figée (« pas
    /// très fluide »). Ce qui est affiché glisse vers <paramref name="valeur"/> (la moitié du chemin en un dixième de
    /// seconde environ), puis avance lentement vers <paramref name="plafond"/>, la fin de l'étape en cours, sans jamais le
    /// dépasser : elle ne promet rien que l'analyse n'a pas fait. Une valeur qui baisse (nouveau scan) est suivie aussitôt.</summary>
    public static double Suivant(double affiche, double valeur, double plafond, double dt, double vitesse = 0)
    {
        if (valeur < affiche - 1e-9 && (plafond <= valeur || affiche > Math.Max(valeur, plafond))) return valeur;
        if (affiche < valeur) return Math.Min(valeur, affiche + (valeur - affiche) * (1 - Math.Exp(-dt * 7)) + 0.0005);
        var haut = Math.Max(valeur, plafond);
        if (affiche >= haut) return affiche;
        // Avec une vitesse (part de jauge par seconde, tirée de la durée attendue de l'étape), la
        // jauge avance au rythme réel de l'analyse ; sans elle, l'ancienne approche lente.
        if (vitesse > 0) return Math.Min(haut, affiche + vitesse * dt);
        return Math.Min(haut, affiche + (haut - affiche) * (1 - Math.Exp(-dt * 0.25)));
    }

    public static readonly DependencyProperty VitesseProperty = DependencyProperty.Register(
        nameof(Vitesse), typeof(double), typeof(ArcDeProgression), new FrameworkPropertyMetadata(0.0));

    /// <summary>Part de la jauge par seconde pendant l'étape en cours (0 : approche lente).</summary>
    public double Vitesse { get => (double)GetValue(VitesseProperty); set => SetValue(VitesseProperty, value); }

    public static readonly DependencyProperty PlafondProperty = DependencyProperty.Register(
        nameof(Plafond), typeof(double), typeof(ArcDeProgression), new FrameworkPropertyMetadata(0.0));

    /// <summary>Jusqu'où la jauge peut avancer seule pendant une étape : la fin de l'étape en cours.</summary>
    public double Plafond { get => (double)GetValue(PlafondProperty); set => SetValue(PlafondProperty, value); }

    public static readonly DependencyProperty ValeurProperty = DependencyProperty.Register(
        nameof(Valeur), typeof(double), typeof(ArcDeProgression), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EpaisseurProperty = DependencyProperty.Register(
        nameof(Epaisseur), typeof(double), typeof(ArcDeProgression), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TraitProperty = DependencyProperty.Register(
        nameof(Trait), typeof(Brush), typeof(ArcDeProgression), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FondProperty = DependencyProperty.Register(
        nameof(Fond), typeof(Brush), typeof(ArcDeProgression), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TourneProperty = DependencyProperty.Register(
        nameof(Tourne), typeof(bool), typeof(ArcDeProgression), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Valeur { get => (double)GetValue(ValeurProperty); set => SetValue(ValeurProperty, value); }
    public double Epaisseur { get => (double)GetValue(EpaisseurProperty); set => SetValue(EpaisseurProperty, value); }
    public Brush Trait { get => (Brush)GetValue(TraitProperty); set => SetValue(TraitProperty, value); }
    public Brush? Fond { get => (Brush?)GetValue(FondProperty); set => SetValue(FondProperty, value); }
    public bool Tourne { get => (bool)GetValue(TourneProperty); set => SetValue(TourneProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var rayon = Math.Min(ActualWidth, ActualHeight) / 2 - Epaisseur / 2;
        if (rayon <= 0) return;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);

        if (Fond is not null)
            dc.DrawEllipse(null, new Pen(Fond, Epaisseur), centre, rayon, rayon);

        // Sans horloge (animations figées, rendu de test), on montre la vraie valeur.
        var affichee = MoteurDAnimation.ActifPourIndicateur ? _affiche : Valeur;
        var part = Tourne ? 0.1 : Math.Clamp(affichee, 0, 1);
        if (part <= 0.001) return;

        var plume = new Pen(Trait, Epaisseur) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (part >= 0.999)
        {
            dc.DrawEllipse(null, plume, centre, rayon, rayon);
            return;
        }

        var depart = (Tourne ? _rotation : 0) - 90;
        var arrivee = depart + part * 360;
        Point Sur(double degres) => new(
            centre.X + rayon * Math.Cos(degres * Math.PI / 180),
            centre.Y + rayon * Math.Sin(degres * Math.PI / 180));

        var geometrie = new StreamGeometry();
        using (var g = geometrie.Open())
        {
            g.BeginFigure(Sur(depart), false, false);
            g.ArcTo(Sur(arrivee), new Size(rayon, rayon), 0, part > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometrie.Freeze();
        dc.DrawGeometry(null, plume, geometrie);
    }
}

/// <summary>Place ses enfants sur une orbite, chacun centré sur la même position qu'un pôle du
/// cercle des composants (voir <see cref="Orbite"/>), là où le shader des tentacules pose leurs bouts.</summary>
public sealed class PanneauOrbital : Panel
{
    public static readonly DependencyProperty RayonXProperty = DependencyProperty.Register(
        nameof(RayonX), typeof(double), typeof(PanneauOrbital), new FrameworkPropertyMetadata(0.34, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty RayonYProperty = DependencyProperty.Register(
        nameof(RayonY), typeof(double), typeof(PanneauOrbital), new FrameworkPropertyMetadata(0.34, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty AngleDepartProperty = DependencyProperty.Register(
        nameof(AngleDepart), typeof(double), typeof(PanneauOrbital), new FrameworkPropertyMetadata(-90.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty DecalageYProperty = DependencyProperty.Register(
        nameof(DecalageY), typeof(double), typeof(PanneauOrbital), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double RayonX { get => (double)GetValue(RayonXProperty); set => SetValue(RayonXProperty, value); }
    public double RayonY { get => (double)GetValue(RayonYProperty); set => SetValue(RayonYProperty, value); }
    public double AngleDepart { get => (double)GetValue(AngleDepartProperty); set => SetValue(AngleDepartProperty, value); }

    /// <summary>Décalage vertical du libellé par rapport à son pôle (vers le bas si positif).</summary>
    public double DecalageY { get => (double)GetValue(DecalageYProperty); set => SetValue(DecalageYProperty, value); }

    /// <summary>Le libellé du haut est posé par son bord bas sur son pôle (son icône, en bas, fait face au centre)
    /// au lieu d'être centré dessous ; les autres ne bougent pas.</summary>
    public static readonly DependencyProperty PortVersCentreProperty = DependencyProperty.Register(
        nameof(PortVersCentre), typeof(bool), typeof(PanneauOrbital), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    public bool PortVersCentre { get => (bool)GetValue(PortVersCentreProperty); set => SetValue(PortVersCentreProperty, value); }

    /// <summary>Centrée sur son pôle, une
    /// carte voyait son icône bouger avec la hauteur de son texte (une ligne d'état de plus après le scan la remontait de
    /// 10 px), loin de la douille fixe du shader. Posée par son icône, chaque carte met le centre de l'icône sur son pôle.
    /// La valeur est la distance du bord de la carte au centre de l'icône (marge + demi-icône). NaN : ancienne mise en page.</summary>
    public static readonly DependencyProperty CentreIconeProperty = DependencyProperty.Register(
        nameof(CentreIcone), typeof(double), typeof(PanneauOrbital), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double CentreIcone { get => (double)GetValue(CentreIconeProperty); set => SetValue(CentreIconeProperty, value); }

    /// <summary>Posé par le panneau sur le libellé du haut (hérité) : son gabarit met l'icône en bas, face à la bille.</summary>
    public static readonly DependencyProperty EnHautProperty = DependencyProperty.RegisterAttached(
        "EnHaut", typeof(bool), typeof(PanneauOrbital), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetEnHaut(DependencyObject o) => (bool)o.GetValue(EnHautProperty);
    public static void SetEnHaut(DependencyObject o, bool valeur) => o.SetValue(EnHautProperty, valeur);

    private bool EstEnHaut(int i, int n) =>
        PortVersCentre && Math.Sin((AngleDepart + 360.0 * i / Math.Max(1, n)) * Math.PI / 180) < -0.9;

    protected override Size MeasureOverride(Size disponible)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var enfant = InternalChildren[i];
            SetEnHaut(enfant, EstEnHaut(i, InternalChildren.Count));
            enfant.Measure(new Size(220, double.PositiveInfinity));
        }
        return new Size(double.IsInfinity(disponible.Width) ? 400 : disponible.Width,
            double.IsInfinity(disponible.Height) ? 400 : disponible.Height);
    }

    /// <summary>Le haut de la carte. Posée par son icône : l'icône du haut est en bas de sa carte, les autres en haut.</summary>
    internal double PositionVerticale(double pole, double hauteur, bool enHaut) => double.IsNaN(CentreIcone)
        ? (enHaut ? pole - hauteur : pole - hauteur / 2 + DecalageY)
        : (enHaut ? pole - hauteur + CentreIcone : pole - CentreIcone);

    protected override Size ArrangeOverride(Size finale)
    {
        var n = InternalChildren.Count;
        for (var i = 0; i < n; i++)
        {
            var enfant = InternalChildren[i];
            var p = Orbite.Position(i, n, finale, 0.5, 0.5, RayonX, RayonY, AngleDepart);
            var d = enfant.DesiredSize;
            var x = Math.Clamp(p.X - d.Width / 2, 0, Math.Max(0, finale.Width - d.Width));
            var enHaut = EstEnHaut(i, n);
            var y = PositionVerticale(p.Y, d.Height, enHaut);
            y = Math.Clamp(y, 0, Math.Max(0, finale.Height - d.Height));
            enfant.Arrange(new Rect(new Point(x, y), d));
        }
        return finale;
    }
}

/// <summary>
/// Le reflet qui suit la souris. L'idée vient du « Specular Button » de React Bits ; le code est propre à DrDXT (08/10/2026,
/// Aucune ligne reprise, la licence MIT avec Commons Clause n'est pas compatible avec la GPL).
///
/// Le liseré d'un contrôle est un dégradé ; ce comportement le fait tourner vers le curseur, comme
/// une arête de métal qui accroche la lumière. Au départ du curseur, le reflet revient se poser.
/// Coût : un angle par mouvement de souris, rien quand la souris ne bouge pas.
/// </summary>
public static class Speculaire
{
    public static readonly DependencyProperty SuivreProperty = DependencyProperty.RegisterAttached(
        "Suivre", typeof(bool), typeof(Speculaire), new PropertyMetadata(false, SurSuivre));

    public static bool GetSuivre(DependencyObject d) => (bool)d.GetValue(SuivreProperty);
    public static void SetSuivre(DependencyObject d, bool valeur) => d.SetValue(SuivreProperty, valeur);

    private const double AngleDeRepos = -35;

    private static void SurSuivre(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border bord || e.NewValue is not true) return;

        bord.Loaded += (_, _) => Preparer(bord);
        bord.MouseMove += (_, ev) =>
        {
            if (Rotation(bord) is not { } r) return;
            var p = ev.GetPosition(bord);
            var angle = Math.Atan2(p.Y - bord.ActualHeight / 2, p.X - bord.ActualWidth / 2) * 180 / Math.PI;
            r.BeginAnimation(RotateTransform.AngleProperty, null);
            r.Angle = angle + 90;
        };
        bord.MouseLeave += (_, _) =>
        {
            if (Rotation(bord) is not { } r) return;
            r.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(AngleDeRepos, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
            });
        };
    }

    private static void Preparer(Border bord)
    {
        if (bord.BorderBrush is not GradientBrush brosse || brosse.RelativeTransform is RotateTransform { IsFrozen: false }) return;
        var copie = brosse.Clone();
        copie.RelativeTransform = new RotateTransform(AngleDeRepos, 0.5, 0.5);
        bord.BorderBrush = copie;
    }

    private static RotateTransform? Rotation(Border bord) =>
        (bord.BorderBrush as GradientBrush)?.RelativeTransform as RotateTransform;
}

/// <summary>
/// Choisir un domaine éteignait une ligne et en allumait une autre. Une
/// seule capsule (« Glisseur », dans le gabarit du rail) glisse jusqu'à la ligne choisie en 280 ms, sous le gouverneur ;
/// figée, elle s'y pose d'un coup.
/// </summary>
public static class CapsuleGlissante
{
    public static readonly DependencyProperty ActifProperty = DependencyProperty.RegisterAttached(
        "Actif", typeof(bool), typeof(CapsuleGlissante), new PropertyMetadata(false, SurActif));

    public static bool GetActif(DependencyObject d) => (bool)d.GetValue(ActifProperty);
    public static void SetActif(DependencyObject d, bool valeur) => d.SetValue(ActifProperty, valeur);

    private static void SurActif(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox rail || e.NewValue is not true) return;
        rail.SelectionChanged += (_, _) => Placer(rail, MoteurDAnimation.Actif);
        // Au chargement et quand la largeur change, la capsule se recale sans glisser.
        rail.LayoutUpdated += (_, _) => Placer(rail, animer: false, seulementSiDecalee: true);
    }

    private static void Placer(ListBox rail, bool animer, bool seulementSiDecalee = false)
    {
        if (rail.Template?.FindName("Glisseur", rail) is not Border capsule) return;
        rail.UpdateLayout();
        if (rail.SelectedIndex < 0 || rail.ItemContainerGenerator.ContainerFromIndex(rail.SelectedIndex) is not FrameworkElement ligne
            || VisualTreeHelper.GetParent(capsule) is not UIElement fond)
        {
            capsule.Opacity = 0;
            return;
        }

        var haut = ligne.TranslatePoint(new Point(0, 0), fond).Y;
        // Une transformation à elle, jamais gelée (celle d'un gabarit l'est, et ne s'anime pas).
        if (capsule.RenderTransform is not TranslateTransform { IsFrozen: false } glisse) capsule.RenderTransform = glisse = new TranslateTransform();
        if (seulementSiDecalee && capsule.Opacity == 1 && Math.Abs(capsule.Height - ligne.ActualHeight) < 0.5
            && (glisse.HasAnimatedProperties || Math.Abs(glisse.Y - haut) < 0.5)) return;

        capsule.Height = ligne.ActualHeight;
        capsule.Width = ligne.ActualWidth;
        var apparait = capsule.Opacity < 1;
        capsule.Opacity = 1;
        if (animer && !apparait)
            glisse.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(haut, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            glisse.BeginAnimation(TranslateTransform.YProperty, null);
            glisse.Y = haut;
        }
    }
}

/// <summary>
/// Le reflet d'Optimiser, une fois, quand le bouton apparaît avec le résultat. Il joue sans geste du joueur : il passe donc
/// par le <see cref="MoteurDAnimation"/>, et ne part pas quand Windows coupe les effets ou que la fenêtre est derrière le jeu.
/// </summary>
public static class RefletALApparition
{
    public static readonly DependencyProperty ActifProperty = DependencyProperty.RegisterAttached(
        "Actif", typeof(bool), typeof(RefletALApparition), new PropertyMetadata(false, SurActif));

    public static bool GetActif(DependencyObject d) => (bool)d.GetValue(ActifProperty);
    public static void SetActif(DependencyObject d, bool valeur) => d.SetValue(ActifProperty, valeur);

    private static readonly Duration Duree = new(TimeSpan.FromMilliseconds(700));

    private static void SurActif(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control bouton || e.NewValue is not true) return;
        // Désabonné d'abord : un style réappliqué au même bouton ne double pas le reflet.
        bouton.IsVisibleChanged -= SurVisible;
        bouton.IsVisibleChanged += SurVisible;
    }

    private static readonly DependencyProperty DejaJoueProperty = DependencyProperty.RegisterAttached(
        "DejaJoue", typeof(bool), typeof(RefletALApparition), new PropertyMetadata(false));

    private static void SurVisible(object sender, DependencyPropertyChangedEventArgs v)
    {
        if (v.NewValue is true && sender is Control bouton) AuPremierAffichage(bouton, MoteurDAnimation.Actif);
    }

    /// <summary>Une fois par bouton et par session : ni revenir sur la page ni un nouveau scan ne le rejouent (une animation
    /// qui revient souvent lasse).</summary>
    internal static bool AuPremierAffichage(Control bouton, bool animer)
    {
        if ((bool)bouton.GetValue(DejaJoueProperty) || !Jouer(bouton, animer)) return false;
        bouton.SetValue(DejaJoueProperty, true);
        return true;
    }

    /// <summary>Lance le reflet sur l'élément « Eclat » du gabarit ; faux quand rien ne joue. Le gabarit est construit
    /// d'abord : à sa première apparition, un bouton jamais mesuré n'en a pas encore. Le reflet attend la fin du masque de
    /// <see cref="Revelation"/> (son retard, puis l'essentiel du dévoilement) : il jouait sous le masque.</summary>
    internal static bool Jouer(Control bouton, bool animer)
    {
        if (!animer) return false;
        bouton.ApplyTemplate();
        if (bouton.Template?.FindName("Eclat", bouton) is not FrameworkElement eclat) return false;
        var depart = TimeSpan.FromMilliseconds(Revelation.GetActive(bouton) ? Revelation.GetRetard(bouton) + 300 : 0);
        var opacite = new DoubleAnimationUsingKeyFrames { Duration = Duree, BeginTime = depart };
        opacite.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacite.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
        opacite.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(550))));
        opacite.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
        eclat.BeginAnimation(UIElement.OpacityProperty, opacite);
        if (eclat.RenderTransform is TranslateTransform glisse && !glisse.IsFrozen)
            glisse.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-120, 420, Duree) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, BeginTime = depart });
        return true;
    }
}

/// <summary>
/// Un chiffre qui compte. Le premier nombre de la phrase monte de 0 à sa
/// valeur en 300 ms ; la fin est toujours la vraie phrase, et le nom lu par un lecteur d'écran l'est dès le départ.
/// Se pose à la place de <c>Text</c> (une liaison sur <c>Text</c> serait écrasée pendant l'animation), sous le gouverneur.
/// </summary>
public static class Compteur
{
    public static readonly DependencyProperty TexteProperty = DependencyProperty.RegisterAttached(
        "Texte", typeof(string), typeof(Compteur),
        new PropertyMetadata(null, (d, e) => { if (d is TextBlock t) Poser(t, e.NewValue as string ?? "", MoteurDAnimation.Actif); }));

    public static string? GetTexte(DependencyObject d) => (string?)d.GetValue(TexteProperty);
    public static void SetTexte(DependencyObject d, string? valeur) => d.SetValue(TexteProperty, valeur);

    private static readonly System.Text.RegularExpressions.Regex Nombre = new(@"\d+");
    internal static readonly TimeSpan Duree = TimeSpan.FromMilliseconds(300);

    /// <summary>La phrase à l'avancement donné : le premier nombre ramené à sa fraction, le reste inchangé.</summary>
    internal static string Etape(string texte, double avancement)
    {
        var m = Nombre.Match(texte);
        if (!m.Success || avancement >= 1) return texte;
        var valeur = (int)Math.Round(int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) * Math.Clamp(avancement, 0, 1));
        return texte[..m.Index] + valeur.ToString(System.Globalization.CultureInfo.InvariantCulture) + texte[(m.Index + m.Length)..];
    }

    internal static void Poser(TextBlock t, string texte, bool animer)
    {
        System.Windows.Automation.AutomationProperties.SetName(t, texte);
        // Une valeur inchangée (le même résultat après un nouveau scan) ne rejoue rien.
        if (!animer || !Nombre.IsMatch(texte) || t.Text == texte) { t.Text = texte; return; }

        t.Text = Etape(texte, 0);
        // Une animation WPF, pas une horloge à soi : elle s'arrête avec la fenêtre, comme l'entrée en scène.
        var montee = new DoubleAnimation(0, 1, Duree) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        montee.Completed += (_, _) => { if (GetTexte(t) == texte || GetTexte(t) is null) t.Text = texte; };
        t.BeginAnimation(AvancementProperty, montee);
    }

    /// <summary>L'avancement de la montée, de 0 à 1 : chaque valeur réécrit le chiffre en route.</summary>
    private static readonly DependencyProperty AvancementProperty = DependencyProperty.RegisterAttached(
        "Avancement", typeof(double), typeof(Compteur),
        new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is TextBlock t && (GetTexte(t) ?? System.Windows.Automation.AutomationProperties.GetName(t)) is { } texte)
                t.Text = Etape(texte, (double)e.NewValue);
        }));
}

/// <summary>
/// « des animations partout ». Changer de domaine dans le rail remplaçait le panneau d'un coup,
/// <see cref="Revelation"/> ne jouant qu'à l'apparition. Au changement de contenu, le panneau entre par un fondu
/// et un glissement de 14 px en 280 ms, seulement si le <see cref="MoteurDAnimation"/> l'autorise.
/// </summary>
public static class TransitionDeContenu
{
    public static readonly DependencyProperty ActifProperty = DependencyProperty.RegisterAttached(
        "Actif", typeof(bool), typeof(TransitionDeContenu), new PropertyMetadata(false, SurActif));

    public static bool GetActif(DependencyObject d) => (bool)d.GetValue(ActifProperty);
    public static void SetActif(DependencyObject d, bool valeur) => d.SetValue(ActifProperty, valeur);

    private static void SurActif(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl panneau || e.NewValue is not true) return;
        // AddValueChanged garde le panneau en vie : acceptable pour un seul panneau, qui vit autant que l'écran.
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(ContentControl.ContentProperty, typeof(ContentControl))
            .AddValueChanged(panneau, (_, _) => Jouer(panneau, MoteurDAnimation.Actif));
    }

    internal static void Jouer(FrameworkElement panneau, bool autorise)
    {
        if (!autorise) return;
        var glisse = new TranslateTransform(0, 14);
        panneau.RenderTransform = glisse;
        var duree = TimeSpan.FromMilliseconds(280);
        var courbe = new CubicEase { EasingMode = EasingMode.EaseOut };
        panneau.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duree) { EasingFunction = courbe });
        glisse.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duree) { EasingFunction = courbe });
    }
}

/// <summary>
/// L'entrée en scène : un voile qui glisse et découvre le contenu quand il devient visible.
/// Une seule fois par apparition, 520 ms, et rien du tout si les animations sont coupées.
/// </summary>
public static class Revelation
{
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(bool), typeof(Revelation), new PropertyMetadata(false, SurActive));

    public static bool GetActive(DependencyObject d) => (bool)d.GetValue(ActiveProperty);
    public static void SetActive(DependencyObject d, bool valeur) => d.SetValue(ActiveProperty, valeur);

    /// <summary>Décalage de l'entrée, en millisecondes. Les cartes d'un même écran entrent
    /// l'une après l'autre, dans l'ordre de lecture, au lieu d'apparaître d'un bloc.</summary>
    public static readonly DependencyProperty RetardProperty = DependencyProperty.RegisterAttached(
        "Retard", typeof(double), typeof(Revelation), new PropertyMetadata(0.0));

    public static double GetRetard(DependencyObject d) => (double)d.GetValue(RetardProperty);
    public static void SetRetard(DependencyObject d, double valeur) => d.SetValue(RetardProperty, valeur);

    private static void SurActive(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not true) return;
        el.IsVisibleChanged += (_, ev) =>
        {
            if (ev.NewValue is true && MoteurDAnimation.Actif) Jouer(el);
        };
    }

    private static void Jouer(FrameworkElement el)
    {
        var bord = new GradientStop(Colors.Black, 0);
        // Avec un retard, la valeur de repos s'affiche jusqu'au départ : à 0,08, le bord gauche de la carte
        // passait déjà en fondu, un liseré fantôme avant l'entrée. À 0, elle reste entièrement masquée.
        var fondu = new GradientStop(Colors.Transparent, GetRetard(el) > 0 ? 0 : 0.08);
        var masque = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0.35) };
        masque.GradientStops.Add(new GradientStop(Colors.Black, 0));
        masque.GradientStops.Add(bord);
        masque.GradientStops.Add(fondu);
        el.OpacityMask = masque;

        var duree = TimeSpan.FromMilliseconds(520);
        var depart = TimeSpan.FromMilliseconds(GetRetard(el));
        var courbe = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
        var a1 = new DoubleAnimation(0, 1.0, duree) { EasingFunction = courbe, BeginTime = depart };
        var a2 = new DoubleAnimation(0.08, 1.08, duree) { EasingFunction = courbe, BeginTime = depart };
        a2.Completed += (_, _) => el.OpacityMask = null;
        bord.BeginAnimation(GradientStop.OffsetProperty, a1);
        fondu.BeginAnimation(GradientStop.OffsetProperty, a2);
    }
}

/// <summary>
/// En lisant un long domaine, le rail partait avec la page et laissait une colonne
/// vide. Posé sur un élément, il le fait suivre la lecture dans la vue défilante, sans sortir de sa section (le
/// parent direct). Un simple décalage de rendu : la mise en page ne bouge pas.
/// </summary>
public static class RailCollant
{
    public static readonly DependencyProperty ActifProperty = DependencyProperty.RegisterAttached(
        "Actif", typeof(bool), typeof(RailCollant), new PropertyMetadata(false, SurActif));

    public static bool GetActif(DependencyObject d) => (bool)d.GetValue(ActifProperty);
    public static void SetActif(DependencyObject d, bool valeur) => d.SetValue(ActifProperty, valeur);

    /// <summary>Le décalage vertical du rail : ce que sa section a déjà passé du haut de la vue, plus une marge,
    /// borné entre 0 et le bas de la section.</summary>
    public static double Decalage(double hautDeSection, double hauteurDeSection, double hauteurDuRail, double marge)
    {
        if (hauteurDeSection <= hauteurDuRail) return 0;
        return Math.Clamp(-hautDeSection + marge, 0, hauteurDeSection - hauteurDuRail);
    }

    private static void SurActif(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not true) return;
        var decalage = new TranslateTransform();
        el.RenderTransform = decalage;
        el.Loaded += (_, _) =>
        {
            var vue = Ancetre<ScrollViewer>(el);
            if (vue is null || VisualTreeHelper.GetParent(el) is not FrameworkElement section) return;
            void Suivre()
            {
                if (!section.IsVisible || !el.IsVisible) return;
                var haut = section.TranslatePoint(new Point(0, 0), vue).Y;
                decalage.Y = Decalage(haut, section.ActualHeight, el.ActualHeight, 16);
            }
            vue.ScrollChanged += (_, _) => Suivre();
            section.SizeChanged += (_, _) => Suivre();
            Suivre();
        };
    }

    private static T? Ancetre<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }
}
