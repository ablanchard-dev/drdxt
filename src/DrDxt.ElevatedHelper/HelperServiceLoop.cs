using System.Runtime.Versioning;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// HelperServiceLoop — the long-lived, multi-connection accept loop the elevated
// helper runs in --service mode (M5 reconciler↔helper channel).
//
// The helper service must serve MANY sequential connections over its lifetime (the
// App AND the reconciler host connect, repeatedly), without changing what one
// connection does. So this loop drives the PROVEN per-connection pipeline — an
// injected AcceptOneAsync delegate (HandshakeAcceptingServer.AcceptOneAsync) that
// already does: (re)create the FIRST_PIPE_INSTANCE ACL'd pipe → accept ONE client →
// handshake (auth → ephemeral key) → serve one typed op → close. This class adds
// Only the outer "loop and re-arm + harden" concern; it touches nothing in the
// per-connection protocol (HMAC, nonce, codec, allowlist, executor).
//
// Hardening (same shape as the R1 accept-loop in Program.cs, now reusable + tested):
//   * a single bad/garbage connection (the accept delegate throws) is LOGGED and the
//     loop CONTINUES — one bad client never kills the service;
//   * a SUSTAINED burst of faults backs off between attempts and then FAILS CLOSED
//     (returns a non-zero code) rather than spinning a CPU-burning tight loop;
//   * a CLEAN accept resets the consecutive-fault counter (transient blips never
//     accumulate to the threshold);
//   * cancellation (the stop signal) unwinds cleanly — an OperationCanceledException
//     from the accept is a shutdown, not a fault.
//
// The accept + backoff are seams so the loop is fully unit-tested with fakes (no
// pipe, no elevation). Program.cs wires the real AcceptOneAsync + a Task.Delay backoff.
// -----------------------------------------------------------------------

/// <summary>Diagnostics sink for the service accept loop. Kept tiny + injectable so
/// the loop is testable; production writes to the helper's console/diagnostics.</summary>
public interface IServiceLoopLog
{
    /// <summary>A single accept faulted (logged, the loop continues).</summary>
    void Fault(int consecutive, int max, string detail);

    /// <summary>The fault threshold was hit; the loop stops fail-closed.</summary>
    void FailedClosed();
}

[SupportedOSPlatform("windows")]
public sealed class HelperServiceLoop
{
    /// <summary>Exit code when the loop fails closed after a sustained fault burst.</summary>
    public const int FailClosedExitCode = 3;

    private readonly Func<CancellationToken, Task> _acceptOne;
    private readonly IServiceLoopLog _log;
    private readonly Func<CancellationToken, Task> _backoff;
    private readonly int _maxConsecutiveFaults;

    /// <param name="acceptOne">Serves exactly ONE connection (the proven per-connection
    /// pipeline). Re-invoked each iteration to serve the next connection.</param>
    /// <param name="log">Fault / fail-closed diagnostics sink.</param>
    /// <param name="backoff">Awaited between consecutive faults to avoid a tight spin
    /// (production = a short Task.Delay; tests pass a no-op or a counter).</param>
    /// <param name="maxConsecutiveFaults">Fail-closed threshold: this many faults in a
    /// row (no clean accept between) stops the loop.</param>
    public HelperServiceLoop(
        Func<CancellationToken, Task> acceptOne,
        IServiceLoopLog log,
        Func<CancellationToken, Task> backoff,
        int maxConsecutiveFaults = 20)
    {
        _acceptOne = acceptOne ?? throw new ArgumentNullException(nameof(acceptOne));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        if (maxConsecutiveFaults < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConsecutiveFaults));
        _maxConsecutiveFaults = maxConsecutiveFaults;
    }

    /// <summary>Runs the accept loop until cancellation or a fault burst. Returns 0 on
    /// a clean (cancelled) shutdown, or <see cref="FailClosedExitCode"/> if it failed
    /// closed. Never throws across the boundary for a connection-level fault.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var consecutiveFaults = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _acceptOne(ct).ConfigureAwait(false);
                consecutiveFaults = 0; // a clean accept resets the fault counter
            }
            catch (OperationCanceledException)
            {
                break; // stop signal during the accept → clean shutdown, not a fault
            }
            catch (Exception ex)
            {
                // A connection-level fault (garbage on the wire, a pipe squatter, …) must
                // Not crash the service into a tight CPU-burning loop. Log, back off, and
                // if it persists, stop fail-closed rather than spin.
                consecutiveFaults++;
                _log.Fault(consecutiveFaults, _maxConsecutiveFaults, ex.GetType().Name);

                if (consecutiveFaults >= _maxConsecutiveFaults)
                {
                    _log.FailedClosed();
                    return FailClosedExitCode;
                }

                try
                {
                    await _backoff(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        return 0;
    }
}
