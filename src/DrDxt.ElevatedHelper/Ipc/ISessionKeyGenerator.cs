using System.Security.Cryptography;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// ISessionKeyGenerator — mints the ephemeral per-session HMAC key (§9.13).
//
// Seam so tests can inject a deterministic key and assert exactly what the
// server hands the authenticated client. The production implementation uses a
// cryptographically secure RNG. The key never leaves the elevated host except
// over the authenticated, DACL-protected pipe.
// -----------------------------------------------------------------------
public interface ISessionKeyGenerator
{
    /// <summary>Returns a fresh ephemeral session key (32 bytes).</summary>
    byte[] NewSessionKey();
}

/// <summary>CSPRNG-backed session-key generator (production).</summary>
public sealed class CryptoSessionKeyGenerator : ISessionKeyGenerator
{
    public byte[] NewSessionKey() => RandomNumberGenerator.GetBytes(HandshakeProtocol.SessionKeyBytes);
}
