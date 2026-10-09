using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// T29 — SingleFlightReconcile serializes concurrent trigger calls with a
// SemaphoreSlim(1,1): only ONE reconcile pass runs at a time. Two triggers firing
// together do not double-run overlapping passes — the second waits for the first to
// release, then runs (no overlap, both complete).
// -----------------------------------------------------------------------
public sealed class SingleFlightReconcileTests
{
    [Fact]
    [Trait("Category", "ReconcileGate")]
    public async Task TwoConcurrentTriggers_NeverOverlap_BothComplete()
    {
        var gate = new InstrumentedTick();
        var single = new SingleFlightReconcile(gate.RunAsync);

        // Fire two passes concurrently.
        var a = single.RunTickAsync(CancellationToken.None);
        var b = single.RunTickAsync(CancellationToken.None);

        // Wait until exactly ONE has entered the body. If single-flight is broken the
        // second would also enter; we give it a beat and assert it did not.
        await gate.WaitUntilEntered(1);
        gate.CurrentlyInside.Should().Be(1, "the second pass must be blocked outside while the first holds the slot");

        // Release the first; the second now acquires the slot and runs.
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

    // An instrumented reconcile body that records overlap. Each call increments an
    // "entered" counter and a "currently inside" gauge, then (unless autoRelease)
    // blocks on a semaphore until the test releases exactly one waiter — letting the
    // test hold a pass "inside" and prove the coordinator does not admit a second.
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

        /// <summary>Spins (briefly) until <paramref name="count"/> calls have entered
        /// the body. Deterministic up to a generous timeout so a broken single-flight
        /// (which would over-admit) is caught, not hung forever.</summary>
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
