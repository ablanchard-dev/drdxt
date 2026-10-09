using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// HelperServiceLoop — the long-lived, multi-connection accept loop the helper runs
// in --service mode. It drives the existing per-connection pipeline (an injected
// AcceptOneAsync delegate) over and over for the service lifetime, without changing
// what one connection does (handshake → typed op → close).
//
// Invariants under test (with a fake accept delegate — no pipe, no elevation):
//   * many SEQUENTIAL connections are served until cancellation (the loop re-arms);
//   * one BAD/garbage connection (the delegate throws) is logged + tolerated — the
//     loop continues to the next accept (a single bad client never kills the service);
//   * a SUSTAINED burst of faults backs off and then fails closed (stops) rather
//     than spinning a CPU-burning tight loop;
//   * a clean accept RESETS the consecutive-fault counter (transient blips never
//     accumulate to the fail-closed threshold);
//   * cancellation unwinds cleanly (no throw across the boundary).
// -----------------------------------------------------------------------
public sealed class HelperServiceLoopTests
{
    // A no-op diagnostics sink that counts logged faults.
    private sealed class CountingLog : IServiceLoopLog
    {
        public int Faults { get; private set; }
        public int Stops { get; private set; }
        public void Fault(int consecutive, int max, string detail) => Faults++;
        public void FailedClosed() => Stops++;
    }

    [Fact]
    public async Task Serves_many_sequential_connections_until_cancelled()
    {
        var served = 0;
        using var cts = new CancellationTokenSource();
        var log = new CountingLog();

        var loop = new HelperServiceLoop(
            acceptOne: _ =>
            {
                if (Interlocked.Increment(ref served) >= 5)
                    cts.Cancel(); // stop after the 5th clean accept
                return Task.CompletedTask;
            },
            log: log,
            backoff: _ => Task.CompletedTask);

        var code = await loop.RunAsync(cts.Token);

        served.Should().BeGreaterThanOrEqualTo(5, "the loop re-arms and serves connection after connection");
        code.Should().Be(0, "a cancelled service loop exits cleanly (0)");
        log.Faults.Should().Be(0);
    }

    [Fact]
    public async Task A_single_bad_connection_is_tolerated_and_the_loop_continues()
    {
        var attempt = 0;
        var goodAfterBad = 0;
        using var cts = new CancellationTokenSource();
        var log = new CountingLog();

        var loop = new HelperServiceLoop(
            acceptOne: _ =>
            {
                attempt++;
                if (attempt == 1)
                    throw new IOException("garbage on the wire"); // one bad client
                // subsequent accepts are clean; stop after we've proven we continued
                if (Interlocked.Increment(ref goodAfterBad) >= 2)
                    cts.Cancel();
                return Task.CompletedTask;
            },
            log: log,
            backoff: _ => Task.CompletedTask);

        var code = await loop.RunAsync(cts.Token);

        goodAfterBad.Should().BeGreaterThanOrEqualTo(2, "the loop kept serving after the bad connection");
        log.Faults.Should().Be(1, "the single fault was logged");
        log.Stops.Should().Be(0, "one fault must not fail the service closed");
        code.Should().Be(0);
    }

    [Fact]
    public async Task A_sustained_fault_burst_backs_off_then_fails_closed()
    {
        var backoffs = 0;
        var log = new CountingLog();

        var loop = new HelperServiceLoop(
            acceptOne: _ => throw new IOException("pipe name squatted"),
            log: log,
            backoff: _ => { backoffs++; return Task.CompletedTask; },
            maxConsecutiveFaults: 5);

        var code = await loop.RunAsync(CancellationToken.None);

        code.Should().Be(3, "after the fault burst the loop fails closed (exit 3), never spins forever");
        log.Stops.Should().Be(1);
        log.Faults.Should().Be(5);
        backoffs.Should().Be(4, "it backs off between faults but not after the final fail-closed one");
    }

    [Fact]
    public async Task A_clean_accept_resets_the_consecutive_fault_counter()
    {
        // Pattern: fault, fault, CLEAN, fault, fault, ... — with a threshold of 3 the
        // clean accept must reset the counter so we never reach fail-closed by blips.
        var step = 0;
        var cleanCount = 0;
        using var cts = new CancellationTokenSource();
        var log = new CountingLog();

        var loop = new HelperServiceLoop(
            acceptOne: _ =>
            {
                var s = step++;
                // 2 faults, 1 clean, 2 faults, 1 clean, ... never 3 faults in a row.
                if (s % 3 == 2)
                {
                    if (Interlocked.Increment(ref cleanCount) >= 4)
                        cts.Cancel();
                    return Task.CompletedTask;
                }
                throw new IOException("transient");
            },
            log: log,
            backoff: _ => Task.CompletedTask,
            maxConsecutiveFaults: 3);

        var code = await loop.RunAsync(cts.Token);

        code.Should().Be(0, "the clean accepts reset the counter; it never reaches the fail-closed threshold");
        log.Stops.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_before_any_accept_exits_cleanly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var loop = new HelperServiceLoop(
            acceptOne: _ => Task.CompletedTask,
            log: new CountingLog(),
            backoff: _ => Task.CompletedTask);

        var code = await loop.RunAsync(cts.Token);
        code.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_during_an_accept_unwinds_cleanly()
    {
        using var cts = new CancellationTokenSource();
        var loop = new HelperServiceLoop(
            acceptOne: async ct => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); },
            log: new CountingLog(),
            backoff: _ => Task.CompletedTask);

        var code = await loop.RunAsync(cts.Token);
        code.Should().Be(0, "an OperationCanceledException from the accept is a clean shutdown, not a fault");
    }
}
