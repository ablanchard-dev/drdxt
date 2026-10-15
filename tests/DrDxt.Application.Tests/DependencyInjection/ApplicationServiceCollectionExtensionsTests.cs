using DrDxt.Application.DependencyInjection;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Application.Tests.DependencyInjection;

// -----------------------------------------------------------------------
// Câblage DI. AddDrDxtApplication() enregistre la couche application :
// ISafetyManager, ITweakEngine, chaque ITweak (D/E/F) en IEnumerable<ITweak>,
// ITweakCatalog → TweakCatalog, IStabilityTester, et les coutures SDK (enregistrées
// comme bouchons minces à échec propre ; les vraies implémentations P/Invoke viennent à part).
//
// Le conteneur doit résoudre ITweakEngine et un IEnumerable<ITweak> non vide.
// -----------------------------------------------------------------------
public sealed class ApplicationServiceCollectionExtensionsTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        // Le moteur journalise via IDiagnosticsLog ; une doublure sans effet rend les
        // enregistrements de l'application autonomes (l'hôte App branche le vrai).
        services.AddSingleton<IDiagnosticsLog>(new NoOpDiagnosticsLog());
        services.AddSingleton<IBenchmarkRunner>(new NoOpBenchmarkRunner());
        services.AddDrDxtApplication();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolves_ITweakEngine()
    {
        using var provider = Build();
        provider.GetService<ITweakEngine>().Should().NotBeNull();
    }

    [Fact]
    public void Resolves_non_empty_enumerable_of_tweaks()
    {
        using var provider = Build();
        var tweaks = provider.GetServices<ITweak>().ToList();
        tweaks.Should().NotBeEmpty();
    }

    [Fact]
    public void Resolves_safety_manager_and_catalog()
    {
        using var provider = Build();
        provider.GetService<ISafetyManager>().Should().NotBeNull();
        provider.GetService<ITweakCatalog>().Should().NotBeNull();
    }

    [Fact]
    public void Catalog_sees_the_registered_tweaks()
    {
        using var provider = Build();
        var catalog = provider.GetRequiredService<ITweakCatalog>();
        catalog.All().Should().NotBeEmpty();
        // Les leviers D/E/F sont tous présents et retrouvables par identifiant.
        catalog.All().Select(t => t.Id).Should()
            .Contain(new[] { "latency.reflex", "gpu.undervolt", "fixit.x3d_ccd" });
    }

    [Fact]
    public void Tweaks_resolve_their_seams_without_throwing()
    {
        // Chaque ITweak (y compris ceux du GPU qui veulent des cibles en objets-valeurs) doit être
        // constructible par le conteneur : une couture non enregistrée lèverait ici.
        using var provider = Build();
        var act = () => provider.GetServices<ITweak>().ToList();
        act.Should().NotThrow();
    }

    // --- Doublures minimales des services transverses dont le moteur a besoin. ---

    private sealed class NoOpDiagnosticsLog : IDiagnosticsLog
    {
        public void Change(string tweakId, string from, string to, string actor) { }
        public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(string.Empty));
    }

    private sealed class NoOpBenchmarkRunner : IBenchmarkRunner
    {
        public Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct) =>
            Task.FromResult(Result<BenchmarkResult>.Fail(ErrorCodes.BenchmarkFailed, "no-op"));
    }
}
