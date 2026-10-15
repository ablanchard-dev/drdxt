using DrDxt.Core;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.Measurement.Tests;

public class MeasurementDiTests
{
    /// <summary>Le canal eleve : depuis que le banc sait reparer son propre service de mesure,
    /// il en depend. C'est un vrai couplage, assume, pas un accident de cablage.</summary>
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IElevatedClient>());
        return services;
    }

    [Fact]
    public void Resolves_benchmark_runner()
    {
        var provider = Services()
            .AddDrDxtMeasurement()
            .BuildServiceProvider();

        var runner = provider.GetRequiredService<IBenchmarkRunner>();

        runner.Should().NotBeNull();
        // Ce test attendait `BenchmarkRunner` nu. Le banc du produit est desormais enveloppe :
        // c'est le decorateur qui emet `InstallPresentMonServiceOp` quand le service ne repond
        // pas -- l'op ecrite, allowlistee, testee, et que personne n'envoyait.
        // Et par-dessus, le repli DXGI (PresentMon ne voit plus rien sur la machine de
        // reference). L'auto-reparation doit rester DEDANS, sinon le repli masquerait sa perte.
        runner.Should().BeOfType<DrDxt.Measurement.Frames.RepliDxgiBenchmarkRunner>()
            .Which.PresentMon.Should().BeOfType<SelfHealingBenchmarkRunner>();
    }

    [Fact]
    public void Container_resolves_benchmark_runner_with_real_session()
    {
        var provider = Services()
            .AddDrDxtMeasurement()
            .BuildServiceProvider();

        // L'exécutant se construit sur la vraie session, câblée sur la vraie API P/Invoke.
        provider.GetRequiredService<IBenchmarkRunner>().Should().NotBeNull();

        var session = provider.GetRequiredService<IPresentMonSession>();
        session.Should().BeOfType<PresentMonSession>();
    }
}
