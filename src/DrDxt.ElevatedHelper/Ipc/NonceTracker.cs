namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Anti-replay nonce tracker.
//
// SECURITY: a valid (correctly-HMAC'd) request must not be replayable. Each
// request carries a unique nonce; the server records seen nonces and rejects
// any repeat fail-closed. Thread-safe so a future concurrent accept loop is
// safe. M0 keeps an unbounded in-memory set (sessions are short-lived); M2
// can bound it / window it by time when the helper becomes long-running.
// -----------------------------------------------------------------------
public sealed class NonceTracker
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Atomically records the nonce. Returns true if it was fresh (accept),
    /// false if it was already seen (replay → reject). An empty/whitespace
    /// nonce is treated as a replay (never fresh) so a missing nonce can never
    /// pass the anti-replay check.
    /// </summary>
    public bool TryRegister(string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            return false;

        lock (_gate)
        {
            return _seen.Add(nonce);
        }
    }
}
