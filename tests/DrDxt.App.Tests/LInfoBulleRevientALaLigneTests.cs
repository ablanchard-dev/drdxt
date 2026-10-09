using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// 25/09/2026 : les explications des états lus passent en info-bulle. Le modèle d'info-bulle
/// limite la largeur à 360 et affichait la chaîne sans retour à la ligne : une explication longue
/// (bridage, incident noyau) était coupée à droite. Le TextBlock que WPF génère doit revenir à la ligne.
/// </summary>
public sealed class LInfoBulleRevientALaLigneTests
{
    private static T? Descendant<T>(DependencyObject racine) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(racine); i++)
        {
            var enfant = VisualTreeHelper.GetChild(racine, i);
            if (enfant is T trouve) return trouve;
            if (Descendant<T>(enfant) is { } plusBas) return plusBas;
        }
        return null;
    }

    [Fact]
    public void Une_explication_longue_revient_a_la_ligne()
    {
        var retour = StaApplication.Run(() =>
        {
            var bulle = new ToolTip { Content = new string('x', 40) + " " + new string('y', 400) };
            bulle.Style = (Style)System.Windows.Application.Current.FindResource(typeof(ToolTip));
            bulle.ApplyTemplate();
            bulle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bulle.Arrange(new Rect(bulle.DesiredSize));
            return Descendant<TextBlock>(bulle)?.TextWrapping;
        });

        retour.Should().Be(TextWrapping.Wrap);
    }
}
