using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HandshakeClientCore — the App (unelevated) end of the §9.13 handshake.
//
// Operates on a raw duplex Stream so it is unit-testable with an in-memory pair.
// One round trip:
//   1. send HandshakeHello (protocol marker; no secret)
//   2. read HandshakeResult
//   3. accept only a well-formed Ok=true result carrying a 32-byte key; anything
//      else (Ok=false, missing/short/garbage key, truncated reply) -> fail-clean,
//      NO key.
//
// The returned key seeds the existing TypedNamedPipeElevatedClient — the proven
// typed/HMAC message protocol is unchanged; only the key's provenance is.
// -----------------------------------------------------------------------
public static class HandshakeClientCore
{
    public static async Task<Result<byte[]>> RunAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            // 1. Greet the server with the protocol marker only.
            await WireCodec.WriteMessageAsync(stream, new HandshakeHello(), ct).ConfigureAwait(false);

            // 2. Read the outcome (bounded; null on EOF/garbage/oversize).
            var result = await WireCodec.ReadMessageAsync<HandshakeResult>(stream, ct).ConfigureAwait(false);
            if (result is null)
                return Fail("No handshake response from the elevated helper.");

            // 3. Fail-closed unless the server explicitly authorized us with a key.
            if (!result.Ok)
                return Fail("Elevated helper refused the handshake (caller not authorized).");
            if (string.IsNullOrEmpty(result.SessionKeyBase64))
                return Fail("Handshake succeeded but no session key was provided.");

            byte[] key;
            try
            {
                key = Convert.FromBase64String(result.SessionKeyBase64);
            }
            catch (FormatException)
            {
                return Fail("Handshake session key was malformed.");
            }

            if (key.Length != HandshakeProtocol.SessionKeyBytes)
                return Fail("Handshake session key had an unexpected size.");

            return Result<byte[]>.Ok(key);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Fail("Handshake transport failed.");
        }
    }

    private static Result<byte[]> Fail(string why) => Result<byte[]>.Fail(ErrorCodes.IoFailure, why);
}
