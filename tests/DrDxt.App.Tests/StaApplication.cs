using System;
using System.Threading;
using System.Windows;

namespace DrDxt.App.Tests;

/// <summary>
/// Joue un délégué sur un thread STA dédié sous une <see cref="Application"/> WPF vivante
/// qui charge le dictionnaire de ressources d'App.xaml, pour que les vues qui dépendent de
/// jetons StaticResource puissent être instanciées dans un test.
/// </summary>
internal static class StaApplication
{
    public static T Run<T>(Func<T> action)
    {
        T result = default!;
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationWithResources();
                // Le lecteur XAML de WPF partage des caches entre fils. Deux vues construites en même
                // temps sur deux fils STA levaient « clé 'RelativeSource' absente » ou « chemin 'Opacity' non
                // résolu », au hasard de la charge. L'app n'a qu'un fil d'interface ; le banc en fait autant.
                lock (Rendu) result = action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
            throw new InvalidOperationException("STA view smoke test failed.", captured);

        return result;
    }

    // WPF n'admet qu'une Application par AppDomain, et xUnit lance les classes de tests
    // en parallele : deux fils STA voyaient tous les deux Current == null et le second
    // levait « Impossible de creer plusieurs instances System.Windows.Application ». Le
    // verrou rend la creation atomique ; les fils suivants reutilisent l'instance vivante.
    private static readonly object Verrou = new();
    private static readonly object Rendu = new();

    private static void EnsureApplicationWithResources()
    {
        lock (Verrou)
        {
            EnsureApplicationWithResourcesCore();
        }
    }

    private static void EnsureApplicationWithResourcesCore()
    {
        if (System.Windows.Application.Current is not null)
            return;

        // Construire l'App et appeler son InitializeComponent() généré, qui charge les jetons de
        // ressources d'App.xaml (pinceaux, polices) dans Application.Current.Resources.
        // OnStartup n'est pas appelé (il ne tourne que sur Application.Run), donc ni fenêtre ni
        // amorçage DI : on obtient juste les jetons StaticResource auxquels la vue se lie.
        var app = new DrDxt.App.App();
        app.InitializeComponent();
    }
}

