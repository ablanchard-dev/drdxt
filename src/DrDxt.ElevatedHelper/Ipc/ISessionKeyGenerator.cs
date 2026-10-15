using System.Security.Cryptography;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// ISessionKeyGenerator : frappe la clé HMAC éphémère par session.
//
// Couture pour que les tests injectent une clé déterministe et vérifient exactement ce que le
// serveur remet au client authentifié. L'implémentation de production utilise un RNG
// cryptographiquement sûr. La clé ne quitte jamais l'hôte élevé, sauf par le pipe
// authentifié et protégé par DACL.
// -----------------------------------------------------------------------
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
