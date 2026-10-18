using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

// WPF peut lever Loaded deux fois sans Unloaded entre les deux (élément re-parenté, gabarit réappliqué). L'abonnement à
// l'événement statique du moteur n'est pris qu'une fois : sinon, après Unloaded, il garde l'élément en vie jusqu'à la
// fermeture de l'App.
public sealed class UneHorlogeSeDesabonneAuDechargementTests
{
    private static int AbonnementsDe(FrameworkElement element)
    {
        var change = (Delegate?)typeof(MoteurDAnimation).GetField(nameof(MoteurDAnimation.Change), BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null);
        return change?.GetInvocationList().Count(d => d.Target?.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(f => ReferenceEquals(f.GetValue(d.Target), element)) == true) ?? 0;
    }

    [Fact]
    public void Deux_chargements_puis_un_dechargement_ne_laissent_aucun_abonnement()
    {
        int apresChargements = -1, apresDechargement = -1;
        var fil = new Thread(() =>
        {
            var element = new Border();
            MoteurDAnimation.HorlogePour(element, () => { });
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            apresChargements = AbonnementsDe(element);
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            apresDechargement = AbonnementsDe(element);
        });
        fil.SetApartmentState(ApartmentState.STA);
        fil.Start(); fil.Join();

        apresChargements.Should().Be(1, "témoin : l'élément chargé est bien abonné");
        apresDechargement.Should().Be(0);
    }
}
