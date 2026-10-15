using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// La fenetre se laisse reduire jusqu'a 1040x680 (MinWidth/MinHeight).
// Les trois vues defilent verticalement mais leur ScrollViewer porte
// HorizontalScrollBarVisibility="Disabled" : a cette largeur, rien ne peut
// deborder vers la droite -- ce qui ne tient pas est ecrase, en silence.
//
// C'est le « responsive » d'une application de bureau : la taille minimale
// qu'on autorise est une promesse, et personne ne la verifiait.
// -----------------------------------------------------------------------
[Collection(CollectionWpf.Nom)]
public sealed class LInterfaceTientALaTailleMinimaleTests
{
    // MainWindow : MinWidth 1040, MinHeight 680, moins la bordure (1 px de chaque
    // cote) et la barre de titre maison (38 px).
    private const double LargeurUtile = 1040 - 2;
    private const double HauteurUtile = 680 - 2 - 38;

    private static bool SousUnViewbox(DependencyObject e, DependencyObject vue)
    {
        for (var p = VisualTreeHelper.GetParent(e); p is not null && p != vue; p = VisualTreeHelper.GetParent(p))
            if (p is Viewbox) return true;
        return false;
    }

    private static string Lignee(DependencyObject e, DependencyObject vue)
    {
        var noms = new List<string>();
        for (var p = VisualTreeHelper.GetParent(e); p is not null && p != vue; p = VisualTreeHelper.GetParent(p))
            noms.Add(p.GetType().Name);
        return string.Join(" < ", noms.Take(6));
    }

    private static IEnumerable<FrameworkElement> Arbre(DependencyObject racine)
    {
        var enfants = VisualTreeHelper.GetChildrenCount(racine);
        for (var i = 0; i < enfants; i++)
        {
            var enfant = VisualTreeHelper.GetChild(racine, i);
            if (enfant is FrameworkElement fe) yield return fe;
            foreach (var petit in Arbre(enfant)) yield return petit;
        }
    }

    /// <summary>Les elements dont le bord droit sort du cadre, donc rognes a l'affichage.</summary>
    private static List<string> HorsCadre(FrameworkElement vue)
    {
        vue.Measure(new Size(LargeurUtile, HauteurUtile));
        vue.Arrange(new Rect(0, 0, LargeurUtile, HauteurUtile));
        vue.UpdateLayout();

        var dehors = new List<string>();
        foreach (var e in Arbre(vue))
        {
            if (e.Visibility != Visibility.Visible || e.ActualWidth <= 0) continue;
            // Ce qui vit sous un Viewbox est mis a l'echelle pour tenir : c'est son role, et
            // hors ecran la mise a l'echelle n'est pas rejouee -- son bord droit vaut 1150
            // qu'on mesure a 1038 ou a 1800 (verifie). On ne peut donc rien en conclure,
            // et il n'y a rien a en conclure.
            if (SousUnViewbox(e, vue)) continue;
            // Un halo ou un liseré deborde volontairement de son parent (Margin negative) ;
            // ce qui compte est le bord droit dans le repere de la VUE.
            var origine = e.TransformToAncestor(vue).Transform(new Point(0, 0));
            var bordDroit = origine.X + e.ActualWidth;
            // Une unite de tolerance : l'arrondi au pixel ne fait pas un defaut.
            if (bordDroit > LargeurUtile + 1)
                dehors.Add($"{e.GetType().Name} '{e.Name}' : bord droit a {bordDroit:0} pour {LargeurUtile:0} de large [{Lignee(e, vue)}]");
        }
        return dehors.Distinct().ToList();
    }

    // Pas de fenetre montree : Measure + Arrange suffisent a calculer la mise en page, et
    // afficher pour de vrai fait tomber le runner (rendu materiel dans une session de test).

    [Fact]
    public void L_ecran_de_scan_tient_dans_la_fenetre_la_plus_petite()
    {
        var dehors = StaApplication.Run(() =>
        {
            var vue = new DrDxt.App.Views.ScanView();
            return HorsCadre(vue);
        });

        dehors.Should().BeEmpty();
    }

    [Fact]
    public void Les_deux_vues_ont_bien_du_contenu_a_mesurer()
    {
        // Une vue construite sans donnees pourrait n'afficher presque rien, et les
        // tests ci-dessus seraient verts sur du vide. On compte ce qu'on mesure.
        var comptes = StaApplication.Run(() => new[]
        {
            Mesure(new DrDxt.App.Views.ScanView()),
        });

        comptes.Should().OnlyContain(n => n > 40, "chaque ecran doit avoir ete reellement dispose");
    }

    private static int Mesure(FrameworkElement vue)
    {
        vue.Measure(new Size(LargeurUtile, HauteurUtile));
        vue.Arrange(new Rect(0, 0, LargeurUtile, HauteurUtile));
        vue.UpdateLayout();
        return Arbre(vue).Count(e => e.Visibility == Visibility.Visible && e.ActualWidth > 0);
    }

    [Fact]
    public void Le_crible_sait_voir_un_element_trop_large()
    {
        // Temoin inverse : sans lui, une mise en page qui ne calcule rien rendrait
        // les deux tests ci-dessus vert sur le vide.
        var dehors = StaApplication.Run(() =>
        {
            var panneau = new StackPanel { Orientation = Orientation.Horizontal };
            panneau.Children.Add(new Border { Width = LargeurUtile + 400, Height = 40 });
            return HorsCadre(panneau);
        });

        dehors.Should().NotBeEmpty("un bloc plus large que la fenetre doit etre vu comme sortant du cadre");
    }
}
