using DrDxt.Core;
using DrDxt.Measurement.Network;
using DrDxt.Measurement.PresentMon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DrDxt.Measurement;

public static class MeasurementServiceCollectionExtensions
{
    public static IServiceCollection AddDrDxtMeasurement(this IServiceCollection services)
    {
        // La vraie frontière P/Invoke derrière la couture testable.
        services.TryAddSingleton<IPresentMonApi, PresentMonApi2>();
        // Une horloge par session : la session est unique, donc deux captures simultanées (relevé de fond et banc du scan)
        // partagent ce chronomètre et le redémarrent l'une pour l'autre. Seul le filet de durée en dépend quand l'instant
        // QPC de la capture est lu.
        services.TryAddTransient<IMonotonicClock, StopwatchClock>();
        // Fabrique explicite : le constructeur de PresentMonSession est interne (il prend la couture
        // interne IPresentMonApi), l'activateur par défaut ne peut donc pas l'atteindre.
        services.TryAddSingleton<IPresentMonSession>(sp =>
            new PresentMonSession(sp.GetRequiredService<IPresentMonApi>(), sp.GetRequiredService<IMonotonicClock>()));
        // Le banc est enveloppe par SelfHealingBenchmarkRunner, et c'est ce cablage qui compte :
        // le decorateur est le seul endroit du produit qui emette `InstallPresentMonServiceOp` : sans
        // lui, l'op serait ecrite, allowlistee, testee et jamais envoyee. Une factory differee : `IElevatedClient` est enregistre par
        // une autre couche, possiblement apres celle-ci.
        // Et s'il ne capture rien, le compteur DXGI prend le relais (Frames/).
        services.TryAddSingleton<IBenchmarkRunner>(sp => new Frames.RepliDxgiBenchmarkRunner(new SelfHealingBenchmarkRunner(
            // La sonde de focus est optionnelle dans le conteneur : elle vit dans Detection, que
            // Measurement ne reference pas. Une couche au-dessus la fournit ; sans elle le focus
            // reste Unknown, et Unknown ne condamne aucune passe.
            new BenchmarkRunner(sp.GetRequiredService<IPresentMonSession>(),
                foreground: sp.GetService<IForegroundProbe>(),
                // Optionnelle, comme le focus : absente, la mesure marche et le materiel reste
                // Unknown. Un materiel non observe ne vaut jamais zero watt.
                materiel: sp.GetService<IHardwareProbe>()),
            sp.GetRequiredService<IElevatedClient>()),
            new Frames.CompteurPresentsDxgi().Mesurer,
            sp.GetService<IForegroundProbe>()));

        // Instrument de latence réseau (ICMP par la BCL ; aucune dépendance, aucun pilote).
        services.TryAddSingleton<INetworkProbe, SystemPingProbe>();
        services.TryAddSingleton<INetworkLatencyMeter, NetworkLatencyMeter>();

        return services;
    }
}
