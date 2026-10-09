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
        // Real P/Invoke boundary behind the testable seam.
        services.TryAddSingleton<IPresentMonApi, PresentMonApi2>();
        // Transient clock: each capture gets a fresh stopwatch (Restart-per-run anyway).
        services.TryAddTransient<IMonotonicClock, StopwatchClock>();
        // Explicit factory: PresentMonSession's ctor is internal (it takes the internal
        // IPresentMonApi seam), so the default activator can't reach it.
        services.TryAddSingleton<IPresentMonSession>(sp =>
            new PresentMonSession(sp.GetRequiredService<IPresentMonApi>(), sp.GetRequiredService<IMonotonicClock>()));
        // Le banc est enveloppe par SelfHealingBenchmarkRunner, et c'est ce cablage qui compte :
        // le decorateur est le seul endroit du produit qui emette `InstallPresentMonServiceOp`.
        // Op ecrite, allowlistee, testee, et jamais envoyee pendant des mois -- exactement le
        // defaut que ce lot repare. Une factory differee : `IElevatedClient` est enregistre par
        // une autre couche, possiblement apres celle-ci.
        // 24/09/2026 — et s'il ne capture rien, le compteur DXGI prend le relais (Frames/).
        services.TryAddSingleton<IBenchmarkRunner>(sp => new Frames.RepliDxgiBenchmarkRunner(new SelfHealingBenchmarkRunner(
            // La sonde de focus est OPTIONNELLE dans le conteneur : elle vit dans Detection, que
            // Measurement ne reference pas. Une couche au-dessus la fournit ; sans elle le focus
            // reste UNKNOWN, et Unknown ne condamne aucune passe.
            new BenchmarkRunner(sp.GetRequiredService<IPresentMonSession>(),
                foreground: sp.GetService<IForegroundProbe>(),
                // Optionnelle, comme le focus : absente, la mesure marche et le materiel reste
                // UNKNOWN. Un materiel non observe ne vaut jamais zero watt.
                materiel: sp.GetService<IHardwareProbe>()),
            sp.GetRequiredService<IElevatedClient>()),
            new Frames.CompteurPresentsDxgi().Mesurer,
            sp.GetService<IForegroundProbe>()));

        // NS-9 — network latency instrument (ICMP through the BCL; no dependency, no driver).
        services.TryAddSingleton<INetworkProbe, SystemPingProbe>();
        services.TryAddSingleton<INetworkLatencyMeter, NetworkLatencyMeter>();

        return services;
    }
}
