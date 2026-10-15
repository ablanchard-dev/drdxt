using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Choisir un domaine éteignait une ligne et en allumait une autre. Une
// seule capsule, sous les lignes, glisse jusqu'à la ligne choisie (posée d'un coup quand le gouverneur fige tout).
public sealed class RailQuiGlisseTests
{
    [Fact]
    public void La_capsule_se_pose_sur_la_ligne_choisie()
    {
        var (capsuleY, ligneY, capsuleH, ligneH, visible) = StaApplication.Run(() =>
        {
            var vue = new DrDxt.App.Views.ScanView();
            var rail = new ListBox { Style = (Style)vue.Resources["RailDomaines"], ItemsSource = new[] { "Processeur", "Carte", "Mémoire", "Réseau" } };
            DrDxt.App.Effets.CapsuleGlissante.SetActif(rail, true);
            rail.Measure(new Size(300, 600));
            rail.Arrange(new Rect(0, 0, 300, 600));
            rail.UpdateLayout();

            rail.SelectedIndex = 2;
            rail.UpdateLayout();

            var capsule = (Border)rail.Template.FindName("Glisseur", rail);
            var ligne = (FrameworkElement)rail.ItemContainerGenerator.ContainerFromIndex(2);
            var haut = ligne.TranslatePoint(new Point(0, 0), (UIElement)VisualTreeHelper.GetParent(capsule)).Y;
            return (((TranslateTransform)capsule.RenderTransform).Y, haut, capsule.Height, ligne.ActualHeight, capsule.Opacity);
        });

        capsuleY.Should().BeApproximately(ligneY, 0.5);
        capsuleH.Should().BeApproximately(ligneH, 0.5);
        ligneY.Should().BeGreaterThan(0, "témoin : la troisième ligne n'est pas en haut");
        visible.Should().Be(1);
    }
}
