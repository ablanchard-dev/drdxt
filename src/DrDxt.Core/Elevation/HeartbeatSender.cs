namespace DrDxt.Core;

// -----------------------------------------------------------------------
// HeartbeatSender — a small REUSABLE disposable that keeps a held tweak's lease
// alive by renewing it on a cadence, until disposed.
//
// CONTRACT (the held-offset apply path is DESIGNED to wire one of these): for the
// duration that a held GPU offset / power limit is applied, while the controller is
// alive the heartbeat renews the lease so the helper's dead-man's-switch never fires.
// When the apply flow completes (or the controlling process dies / disposes it), the
// heartbeat stops; the lease then expires and the watchdog auto-reverts the held tweak.
//
// STATUS: this is not yet wired into the live apply path — the GPU tweaks do not arm a
// lease today (see the lease note in GpuUndervoltTweak); this sender + the
// LeaseWatchdog are exercised by tests only. Once wired, the above contract holds.
//
// Renew goes through IElevatedClient.RenewLease → the same signed IPC pipeline +
// the same caller identity as every other op (the helper only lets the lease OWNER
// renew). A transient renew failure (pipe busy, helper momentarily down) is
// swallowed — a single missed beat must not crash the controller; the lease simply
// has fewer beats before its next genuine expiry.
// -----------------------------------------------------------------------
public sealed class HeartbeatSender : IAsyncDisposable
{
    private readonly IElevatedClient _client;
    private readonly string _leaseId;
    private readonly int _leaseSeconds;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private volatile bool _disposed;
    private int _perdu;

    /// <summary>08/10/2026 (revue n° 13) : le helper a refusé un renouvellement (il ne connaît plus ce bail : il a
    /// expiré et l'origine a été rendue). Levé une fois. Une erreur passagère du transport ne le lève pas.</summary>
    public event Action? Perdu;

    /// <param name="client">The elevated client used to send the renew.</param>
    /// <param name="leaseId">The lease to keep alive (matches the EstablishLeaseOp id).</param>
    /// <param name="leaseSeconds">The expiry extension each renew requests.</param>
    /// <param name="interval">Optional background cadence. Defaults to ~1/3 of the
    /// lease so a couple of misses are tolerated before a genuine expiry.</param>
    public HeartbeatSender(IElevatedClient client, string leaseId, int leaseSeconds, TimeSpan? interval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(leaseId))
            throw new ArgumentException("Lease id must be non-empty.", nameof(leaseId));
        if (leaseSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(leaseSeconds), "Lease duration must be positive.");

        _leaseId = leaseId;
        _leaseSeconds = leaseSeconds;
        _interval = interval ?? TimeSpan.FromSeconds(Math.Max(1, leaseSeconds / 3));
    }

    /// <summary>Sends exactly one renew now. No-op after disposal. Never throws — a
    /// transient failure is swallowed (the next beat retries).</summary>
    public async Task PulseAsync(CancellationToken ct)
    {
        if (_disposed)
            return;
        try
        {
            var r = await _client.RenewLease(_leaseId, _leaseSeconds, ct).ConfigureAwait(false);
            if (r is { IsOk: false, Error.Code: ErrorCodes.ElevationDenied } && Interlocked.Exchange(ref _perdu, 1) == 0)
                Perdu?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Shutdown / caller cancellation — not a heartbeat error.
        }
        catch
        {
            // A transient transport error must not crash the controller.
        }
    }

    /// <summary>Starts the background renew loop on the configured interval. Idempotent.
    /// Each tick sends a pulse until the sender is disposed.</summary>
    public void Start()
    {
        if (_disposed || _loop is not null)
            return;
        _loop = RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await PulseAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal stop on dispose.
        }
    }

    /// <summary>Autocontrôle du 08/10/2026 (SEC1) : l'essai est rendu, le bail n'a plus rien à garder. Le battement s'arrête
    /// et le bail est ramené à une seconde : il expire aussitôt (son retour rejoue un état déjà rendu) et le helper peut
    /// s'arrêter avec l'App au lieu d'attendre l'expiration.</summary>
    public async Task LibererAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
        try { await _client.RenewLease(_leaseId, 1, CancellationToken.None).ConfigureAwait(false); }
        catch { /* bail laissé à son expiration : le helper rend l'origine seul */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _cts.Cancel(); } catch { /* already disposed */ }

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* loop swallows its own; defensive */ }
        }

        _cts.Dispose();
    }
}
