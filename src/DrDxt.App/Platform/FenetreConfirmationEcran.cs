using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.App.Platform;

/// <summary>
/// L-ECRAN : la question que Windows pose lui-même après un changement d'affichage, « garder ou revenir », avec un compte
/// à rebours. Pas de réponse dans le délai, fenêtre fermée, pas de fenêtre possible : non. Un écran qui n'affiche rien ne
/// se confirme pas, et l'ancien mode revient tout seul.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FenetreConfirmationEcran(ILocalizer localizer) : IConfirmationEcran
{
    public Task<bool> GarderAsync(int hz, TimeSpan delai)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return Task.FromResult(false);
            return dispatcher.InvokeAsync(() => Demander(hz, delai)).Task;
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private bool Demander(int hz, TimeSpan delai)
    {
        var garde = false;
        var reste = (int)delai.TotalSeconds;
        var fenetre = new Window
        {
            Title = localizer.Get("Dialog.Ecran.Title"),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
        };
        // 07/10/2026 (revue F-15) : sur un écran noir, Entrée ou Espace tapés à l'aveugle doivent REVENIR, comme
        // Windows. « Revenir » est donc le bouton par défaut et le premier focus ; la fenêtre principale reste bloquée.
        var principale = System.Windows.Application.Current?.MainWindow;
        if (principale is { IsVisible: true }) fenetre.Owner = principale;
        var revenir = new Button { Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4), IsCancel = true, IsDefault = true };
        var garder = new Button { Content = localizer.Get("Dialog.Ecran.Garder"), Padding = new Thickness(12, 4, 12, 4) };
        fenetre.Loaded += (_, _) => revenir.Focus();
        void Compter() => revenir.Content = string.Format(localizer.Get("Dialog.Ecran.Revenir"), reste);
        Compter();
        garder.Click += (_, _) => { garde = true; fenetre.Close(); };
        revenir.Click += (_, _) => fenetre.Close();

        var boutons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        boutons.Children.Add(garder);
        boutons.Children.Add(revenir);
        var corps = new StackPanel { Margin = new Thickness(20) };
        corps.Children.Add(new TextBlock { Text = string.Format(localizer.Get("Dialog.Ecran.Question"), hz), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        corps.Children.Add(boutons);
        fenetre.Content = corps;

        var minuterie = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        minuterie.Tick += (_, _) =>
        {
            reste--;
            if (reste <= 0) fenetre.Close();
            else Compter();
        };
        minuterie.Start();
        fenetre.ShowDialog();
        minuterie.Stop();
        return garde;
    }
}
