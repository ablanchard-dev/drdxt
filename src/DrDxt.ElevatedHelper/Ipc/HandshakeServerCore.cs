using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HandshakeServerCore — the elevated end of the §9.13 session-key handshake.
//
// Runs ONCE per accepted connection, before the typed HMAC protocol. Operates on
// a raw duplex Stream (the pipe) + the already-resolved CallerInfo, so it is
// unit-testable with an in-memory stream pair (no real pipe, nothing elevated).
//
// ORDER (the spec's order — AUTH first, then mint the key):
//   1. read HandshakeHello (bounded, fail-clean on garbage/truncation)
//   2. check the protocol marker
//   3. AUTHENTICATE the caller (the same ICallerAuthenticator the typed server
//      uses: PID -> image identity). DENY -> reply Ok=false, NO key, return Fail.
//   4. only now mint a fresh EPHEMERAL key (CSPRNG) and send it to the verified
//      client. The key is the one the rest of the session's typed messages are
//      HMAC'd with — so the proven message protocol is untouched; only the key's
//      provenance changed (negotiated, not env-var pre-shared).
//
// Fail-closed throughout: any read/parse/auth failure sends Ok=false (best effort)
// and returns Result.Fail with NO key minted. An exception never escapes. The
// failure CODE distinguishes the cause: ELEVATION_DENIED for a well-formed peer
// that failed caller-auth, HANDSHAKE_PROTOCOL_ERROR for a malformed/truncated/
// garbage/unsupported handshake (a transport-layer fault, not an auth denial).
// -----------------------------------------------------------------------
public sealed class HandshakeServerCore
{
    private readonly ICallerAuthenticator _authenticator;
    private readonly ISessionKeyGenerator _keyGenerator;

    public HandshakeServerCore(ICallerAuthenticator authenticator, ISessionKeyGenerator keyGenerator)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _keyGenerator = keyGenerator ?? throw new ArgumentNullException(nameof(keyGenerator));
    }

    /// <summary>Runs the server handshake on <paramref name="stream"/> for the
    /// peer described by <paramref name="caller"/>. On success returns the minted
    /// ephemeral key (the same bytes the client received) so the caller can key
    /// the typed protocol with it. On any failure returns Fail and no key.</summary>
    public async Task<Result<byte[]>> RunAsync(Stream stream, CallerInfo caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            // 1. Read the hello (length-prefixed JSON, bounded; null on EOF/garbage).
            //    A truncated/garbage hello is a TRANSPORT/PROTOCOL fault, not an
            //    authorization denial → HANDSHAKE_PROTOCOL_ERROR.
            var hello = await WireCodec.ReadMessageAsync<HandshakeHello>(stream, ct).ConfigureAwait(false);
            if (hello is null)
                return await DenyAsync(stream, ct, ErrorCodes.HandshakeProtocolError,
                    "No or malformed handshake hello.").ConfigureAwait(false);

            // 2. Reject an incompatible/garbage protocol marker fail-clean (protocol fault).
            if (!string.Equals(hello.Protocol, HandshakeProtocol.Version, StringComparison.Ordinal))
                return await DenyAsync(stream, ct, ErrorCodes.HandshakeProtocolError,
                    "Unsupported handshake protocol.").ConfigureAwait(false);

            // 3. AUTHENTICATE the caller before any key is minted. A non-DrDxt.App
            //    peer is denied here and never receives a key. This is an
            //    AUTHORIZATION denial → ELEVATION_DENIED (distinct from a protocol fault).
            var authz = _authenticator.Authorize(caller);
            if (!authz.IsOk)
                return await DenyAsync(stream, ct, ErrorCodes.ElevationDenied,
                    "Caller not authorized.").ConfigureAwait(false);

            // 4. Caller verified → mint a fresh ephemeral key and hand it over the
            //    authenticated, DACL-protected channel. A key-gen fault is an internal
            //    transport/protocol failure (no auth issue) → HANDSHAKE_PROTOCOL_ERROR.
            var key = _keyGenerator.NewSessionKey();
            if (key is null || key.Length != HandshakeProtocol.SessionKeyBytes)
                return await DenyAsync(stream, ct, ErrorCodes.HandshakeProtocolError,
                    "Session key generation failed.").ConfigureAwait(false);

            var result = new HandshakeResult { Ok = true, SessionKeyBase64 = Convert.ToBase64String(key) };
            await WireCodec.WriteMessageAsync(stream, result, ct).ConfigureAwait(false);
            return Result<byte[]>.Ok(key);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a handshake failure
        }
        catch
        {
            // Any transport/parse fault → fail-closed, no key. This is a protocol/
            // transport failure (not an auth denial) → HANDSHAKE_PROTOCOL_ERROR.
            return await DenyAsync(stream, ct, ErrorCodes.HandshakeProtocolError,
                "Handshake failed.").ConfigureAwait(false);
        }
    }

    // Sends a best-effort Ok=false (the client must fail-closed) and returns Fail
    // with the supplied code: ELEVATION_DENIED for a caller-auth denial,
    // HANDSHAKE_PROTOCOL_ERROR for a transport/garbage/truncated handshake. A write
    // fault here is swallowed — the peer may already be gone; what matters is that
    // NO key was minted/sent.
    private static async Task<Result<byte[]>> DenyAsync(
        Stream stream, CancellationToken ct, string code, string why)
    {
        try
        {
            await WireCodec.WriteMessageAsync(stream, new HandshakeResult { Ok = false }, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Peer vanished mid-deny; nothing actionable.
        }
        return Result<byte[]>.Fail(code, why);
    }
}
