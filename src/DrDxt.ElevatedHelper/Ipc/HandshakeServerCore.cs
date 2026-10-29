using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HandshakeServerCore : le bout élevé du handshake de clé de session, une fois par connexion, avant le protocole HMAC
// typé. Sur un Stream duplex brut et le CallerInfo résolu : testable avec des flux en mémoire. L'auth d'abord, puis la
// clé :
//   1. lire HandshakeHello (borné, échec propre sur déchet ou troncature)
//   2. vérifier le marqueur de protocole
//   3. authentifier l'appelant (le même ICallerAuthenticator que le serveur typé :
//      PID -> identité d'image). Refus -> répondre Ok=false, aucune clé, rendre Fail.
//   4. seulement alors frapper une clé éphémère (CSPRNG) pour le client vérifié : elle signe en HMAC les messages typés
//      de la session, négociée et jamais partagée d'avance par l'environnement.
// Fail-closed : tout échec de lecture, d'analyse ou d'auth envoie Ok=false (au mieux) et rend Fail sans clé, jamais
// d'exception. ELEVATION_DENIED pour un pair bien formé refusé à l'auth, HANDSHAKE_PROTOCOL_ERROR pour un handshake
// malformé, tronqué, déchet ou non pris en charge.
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

    /// <summary>Le handshake serveur sur <paramref name="stream"/> pour <paramref name="caller"/> : en succès, la clé
    /// frappée (les octets reçus par le client) pour signer le protocole typé ; en échec, Fail sans clé.</summary>
    public async Task<Result<byte[]>> RunAsync(Stream stream, CallerInfo caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            // 1. Le hello (borné ; null sur EOF ou déchet) : tronqué ou déchet → HANDSHAKE_PROTOCOL_ERROR.
            var hello = await WireCodec.ReadMessageAsync<HandshakeHello>(stream, ct).ConfigureAwait(false);
            if (hello is null)
                return await DenyAsync(stream, ct, ErrorCodes.HandshakeProtocolError,
                    "No or malformed handshake hello.").ConfigureAwait(false);

            // 2. Rejeter proprement un marqueur de protocole incompatible ou déchet (panne de protocole).
            if (!string.Equals(hello.Protocol, HandshakeProtocol.Version, StringComparison.Ordinal))
                return await DenyAsync(stream, ct, ErrorCodes.HandshakeProtocolError,
                    "Unsupported handshake protocol.").ConfigureAwait(false);

            // 3. Authentifier avant toute clé : un pair qui n'est pas DrDxt.App → ELEVATION_DENIED, jamais de clé.
            var authz = _authenticator.Authorize(caller);
            if (!authz.IsOk)
                return await DenyAsync(stream, ct, ErrorCodes.ElevationDenied,
                    "Caller not authorized.").ConfigureAwait(false);

            // 4. Une clé éphémère neuve par le canal protégé par DACL ; panne de génération → HANDSHAKE_PROTOCOL_ERROR.
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
            throw; // une annulation n'est pas un échec de handshake
        }
        catch
        {
            // Panne de transport ou d'analyse → aucune clé, HANDSHAKE_PROTOCOL_ERROR.
            return await DenyAsync(stream, ct, ErrorCodes.HandshakeProtocolError,
                "Handshake failed.").ConfigureAwait(false);
        }
    }

    // Ok=false au mieux, puis Fail avec le code fourni. Une panne d'écriture est avalée (le pair est peut-être parti) :
    // ce qui compte, c'est qu'aucune clé n'ait été frappée ni envoyée.
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
            // Le pair a disparu en plein refus ; rien à faire.
        }
        return Result<byte[]>.Fail(code, why);
    }
}
