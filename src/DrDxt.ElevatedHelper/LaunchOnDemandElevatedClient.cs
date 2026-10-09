using DrDxt.Core;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// LaunchOnDemandElevatedClient — the App's IElevatedClient that defers the UAC
// elevation to the first privileged op (so the App never prompts at startup),
// then delegates to the real handshaking client.
//
// On the first ExecuteAsync it runs the ensure-running step (HelperLauncher: one
// interactive UAC, or reuse if already up). Once that succeeds, every op (this and
// future) delegates to the inner handshaking client, which negotiates an ephemeral
// session key per connection (§9.13). A failed launch (UAC declined / helper never
// comes up) is surfaced as a clean Fail and is RETRYABLE on a later op — a single
// dismissed prompt must not permanently break apply.
//
// Both seams (ensure-running + inner client) are injected so the gate is fully
// unit-tested without spawning anything. Concurrency-safe: ensure runs at most once
// at a time and is only "latched" after success.
// -----------------------------------------------------------------------
public sealed class LaunchOnDemandElevatedClient : IElevatedClient
{
    private readonly Func<CancellationToken, Task<Result>> _ensureRunningAsync;
    private readonly IElevatedClient _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ensured;

    public LaunchOnDemandElevatedClient(
        Func<CancellationToken, Task<Result>> ensureRunningAsync, IElevatedClient inner)
    {
        _ensureRunningAsync = ensureRunningAsync ?? throw new ArgumentNullException(nameof(ensureRunningAsync));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
    {
        var ensure = await EnsureOnceAsync(ct).ConfigureAwait(false);
        if (!ensure.IsOk)
            return ensure; // launch failed → no channel (retryable next op)

        var r = await _inner.ExecuteAsync(op, ct).ConfigureAwait(false);
        if (r.Error?.Code != ErrorCodes.HelperUnreachable) return r;

        // 08/10/2026 (revue n° 14) : le helper s'arrête seul après 15 minutes d'inactivité (SEC1). « Déjà lancé » ne
        // vaut plus : il est relancé une fois, sur ce geste, et l'op est rejouée.
        _ensured = false;
        ensure = await EnsureOnceAsync(ct).ConfigureAwait(false);
        return ensure.IsOk ? await _inner.ExecuteAsync(op, ct).ConfigureAwait(false) : ensure;
    }

    /// <summary>Une op qui REPOND a besoin du meme helper lance que les autres. Sans ce
    /// relais, une lecture serait tombee sur le refus par defaut de IElevatedClient au lieu
    /// d'atteindre le client interne -- un canal cable de bout en bout, muet a l'avant-dernier
    /// maillon.</summary>
    public async Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
    {
        var ensure = await EnsureOnceAsync(ct).ConfigureAwait(false);
        if (!ensure.IsOk)
            return Result<byte[]>.Fail(ensure.Error!.Value.Code, ensure.Error.Value.Message);

        var lu = await _inner.ReadAsync(op, ct).ConfigureAwait(false);
        if (lu.Error?.Code != ErrorCodes.HelperUnreachable) return lu;
        _ensured = false; // même raison que ExecuteAsync : le helper s'est arrêté pour inactivité
        ensure = await EnsureOnceAsync(ct).ConfigureAwait(false);
        return ensure.IsOk ? await _inner.ReadAsync(op, ct).ConfigureAwait(false)
                           : Result<byte[]>.Fail(ensure.Error!.Value.Code, ensure.Error.Value.Message);
    }

    // Runs the ensure-running step at most once SUCCESSFULLY. A failure does not
    // latch, so a later op can retry (e.g. after the user dismissed the first UAC).
    private async Task<Result> EnsureOnceAsync(CancellationToken ct)
    {
        if (_ensured)
            return Result.Ok();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured)
                return Result.Ok();

            var r = await _ensureRunningAsync(ct).ConfigureAwait(false);
            if (r.IsOk)
                _ensured = true;
            return r;
        }
        finally
        {
            _gate.Release();
        }
    }
}
