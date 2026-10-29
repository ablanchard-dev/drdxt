using System.Security.Cryptography;

namespace DrDxt.ElevatedHelper.Ipc;

// ISessionKeyGenerator : frappe la clé HMAC éphémère par session (RNG cryptographiquement sûr en production ; une clé
// déterministe en test). Elle ne quitte l'hôte élevé que par le pipe authentifié et protégé par DACL.
public interface ISessionKeyGenerator
{
    /// <summary>Rend une clé de session éphémère neuve (32 octets).</summary>
    byte[] NewSessionKey();
}

/// <summary>Générateur de clé de session adossé à un CSPRNG (production).</summary>
public sealed class CryptoSessionKeyGenerator : ISessionKeyGenerator
{
    public byte[] NewSessionKey() => RandomNumberGenerator.GetBytes(HandshakeProtocol.SessionKeyBytes);
}
