using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Critique B10 (passage 2, flexibilité 2/4) : « pas de navigation clavier visible dans le rail ».
// La flèche du bas passe au domaine suivant, et chaque ligne garde le cadre de focus de l'appli.
public sealed class RailAuClavierTests
{
    [Fact]
    public void La_fleche_du_bas_ouvre_le_domaine_suivant()
    {
        var (avant, apres, cadre) = StaApplication.Run(() =>
        {
            var vue = new DrDxt.App.Views.ScanView();
            var rail = new ListBox { Style = (Style)vue.Resources["RailDomaines"], ItemsSource = new[] { "Processeur", "Carte", "Mémoire" } };
            using var source = new HwndSource(new HwndSourceParameters("rail") { Width = 300, Height = 400 }) { RootVisual = rail };
            rail.UpdateLayout();
            rail.SelectedIndex = 0;
            var premiere = (ListBoxItem)rail.ItemContainerGenerator.ContainerFromIndex(0);
            premiere.Focus();
            var avant = rail.SelectedIndex;

            premiere.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
            return (avant, rail.SelectedIndex, premiere.FocusVisualStyle is not null && ReferenceEquals(premiere.FocusVisualStyle, vue.TryFindResource("FocusIris")));
        });

        avant.Should().Be(0, "témoin : on part du premier domaine");
        apres.Should().Be(1);
        cadre.Should().BeTrue("la ligne atteinte au clavier se voit");
    }
}
