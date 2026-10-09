using DrDxt.Application.DependencyInjection;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Application.Tests.DependencyInjection;

// -----------------------------------------------------------------------
// M2 H2 — DI wiring. AddDrDxtApplication() registers the P2 application layer:
// ISafetyManager, ITweakEngine, every ITweak (D/E/F) as IEnumerable<ITweak>,
// ITweakCatalog -> TweakCatalog, IStabilityTester, and the SDK seams (registered
// as thin fail-clean stubs for now — concrete P/Invoke impls land later).
//
// The container must resolve ITweakEngine and a NON-EMPTY IEnumerable<ITweak>.
// -----------------------------------------------------------------------
public sealed class ApplicationServiceCollectionExtensionsTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        // The engine logs through IDiagnosticsLog; register a no-op test double so the
        // application registrations are self-contained (the App host wires the real one).
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
        // The D/E/F tweaks are all present and discoverable by id.
        catalog.All().Select(t => t.Id).Should()
            .Contain(new[] { "latency.reflex", "gpu.undervolt", "fixit.x3d_ccd" });
    }

    [Fact]
    public void Tweaks_resolve_their_seams_without_throwing()
    {
        // Every ITweak (incl. the GPU ones that need value-object targets) must be
        // constructible by the container — a missing seam registration would throw here.
        using var provider = Build();
        var act = () => provider.GetServices<ITweak>().ToList();
        act.Should().NotThrow();
    }

    // --- Minimal test doubles for the cross-cutting services the engine needs. ---

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
