using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// SingleFlightReconcile sérialise les appels concurrents des déclencheurs avec un
// SemaphoreSlim(1,1) : un seul passage de réconciliation à la fois. Deux déclencheurs qui
// jouent ensemble ne doublent pas les passages : le second attend que le premier libère,
// puis tourne (aucun chevauchement, les deux s'achèvent).
// -----------------------------------------------------------------------
public sealed class SingleFlightReconcileTests
{
    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task TwoConcurrentTriggers_NeverOverlap_BothComplete()
    {
        var gate = new InstrumentedTick();
        var single = new SingleFlightReconcile(gate.RunAsync);

        // Lancer deux passages en parallèle.
        var a = single.RunTickAsync(CancellationToken.None);
        var b = single.RunTickAsync(CancellationToken.None);

        // Attendre qu'exactement un soit entré dans le corps. Si le vol unique est cassé, le
        // second entrerait aussi ; on lui laisse un instant et on vérifie qu'il ne l'a pas fait.
        await gate.WaitUntilEntered(1);
        gate.CurrentlyInside.Should().Be(1, "the second pass must be blocked outside while the first holds the slot");

        // Libérer le premier ; le second prend maintenant le créneau et tourne.
        gate.ReleaseOne();
        await gate.WaitUntilEntered(2);
        gate.CurrentlyInside.Should().Be(1, "still only one inside — they are serialized, never overlapping");
        gate.ReleaseOne();

        var results = await Task.WhenAll(a, b);

        results.Should().OnlyContain(r => r.IsOk);
        gate.MaxConcurrency.Should().Be(1, "single-flight must serialize — never two overlapping passes");
        gate.TotalEntered.Should().Be(2, "both triggers must complete, one after the other");
    }

    [Fact]
    public async Task SequentialCalls_EachRun()
    {
        var gate = new InstrumentedTick(autoRelease: true);
        var single = new SingleFlightReconcile(gate.RunAsync);

        (await single.RunTickAsync(default)).IsOk.Should().BeTrue();
        (await single.RunTickAsync(default)).IsOk.Should().BeTrue();

        gate.TotalEntered.Should().Be(2);
        gate.MaxConcurrency.Should().Be(1);
    }

    [Fact]
    public async Task PropagatesTheWrappedResult()
    {
        Task<Result> Fail(CancellationToken ct) =>
            Task.FromResult(Result.Fail(ErrorCodes.IoFailure, "boom"));

        var single = new SingleFlightReconcile(Fail);

        var result = await single.RunTickAsync(default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
    }

    // Un corps de réconciliation instrumenté qui enregistre les chevauchements. Chaque appel
    // incrémente un compteur « entrés » et une jauge « actuellement dedans », puis (sauf autoRelease)
    // bloque sur un sémaphore jusqu'à ce que le test libère exactement un attendant : le test
    // peut ainsi tenir un passage « dedans » et prouver que le coordinateur n'en admet pas un second.
    private sealed class InstrumentedTick
    {
        private readonly bool _autoRelease;
        private readonly SemaphoreSlim _release = new(0);
        private readonly object _lock = new();
        private int _inside;
        private int _max;
        private int _total;

        public InstrumentedTick(bool autoRelease = false) => _autoRelease = autoRelease;

        public int CurrentlyInside { get { lock (_lock) { return _inside; } } }
        public int MaxConcurrency { get { lock (_lock) { return _max; } } }
        public int TotalEntered { get { lock (_lock) { return _total; } } }

        public async Task<Result> RunAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                _total++;
                _inside++;
                if (_inside > _max) _max = _inside;
            }

            if (!_autoRelease)
                await _release.WaitAsync(ct);

            lock (_lock) { _inside--; }
            return Result.Ok();
        }

        public void ReleaseOne() => _release.Release();

        /// <summary>Tourne (brièvement) jusqu'à ce que <paramref name="count"/> appels soient entrés
        /// dans le corps. Déterministe jusqu'à un délai généreux, pour qu'un vol unique cassé
        /// (qui admettrait trop) soit attrapé, pas suspendu pour toujours.</summary>
        public async Task WaitUntilEntered(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (TotalEntered < count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"only {TotalEntered}/{count} entered the tick body");
                await Task.Delay(5);
            }
        }
    }
}
