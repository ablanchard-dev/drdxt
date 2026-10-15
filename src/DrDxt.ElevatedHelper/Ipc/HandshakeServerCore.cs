using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HandshakeServerCore : le bout élevé du handshake de clé de session.
//
// Tourne une fois par connexion acceptée, avant le protocole HMAC typé. Travaille sur un Stream
// duplex brut (le pipe) + le CallerInfo déjà résolu : il se teste donc avec une paire de flux en
// mémoire (pas de vrai pipe, rien d'élevé).
//
// Ordre (celui de la spec : l'auth d'abord, puis la frappe de la clé) :
//   1. lire HandshakeHello (borné, échec propre sur déchet ou troncature)
//   2. vérifier le marqueur de protocole
//   3. authentifier l'appelant (le même ICallerAuthenticator que le serveur typé :
//      PID -> identité d'image). Refus -> répondre Ok=false, aucune clé, rendre Fail.
//   4. seulement alors frapper une clé éphémère neuve (CSPRNG) et l'envoyer au client vérifié.
//      C'est la clé qui signe en HMAC tous les messages typés suivants de la session : le
//      protocole de messages éprouvé reste intact ; seule la provenance de la clé change
//      (négociée, pas partagée d'avance par variable d'environnement).
//
// Fail-closed partout : tout échec de lecture, d'analyse ou d'auth envoie Ok=false (au mieux) et
// rend Result.Fail sans clé frappée. Aucune exception ne s'échappe. Le code d'échec distingue la
// cause : ELEVATION_DENIED pour un pair bien formé qui a échoué à l'auth, HANDSHAKE_PROTOCOL_ERROR
// pour un handshake malformé, tronqué, déchet ou non pris en charge (une panne de transport, pas
// un refus d'auth).
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

    /// <summary>Fait le handshake serveur sur <paramref name="stream"/> pour le pair décrit par
    /// <paramref name="caller"/>. En cas de succès, rend la clé éphémère frappée (les mêmes octets
    /// que ceux reçus par le client) pour que l'appelant en signe le protocole typé. En cas d'échec,
    /// rend Fail et aucune clé.</summary>
    public async Task<Result<byte[]>> RunAsync(Stream stream, CallerInfo caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            // 1. Lire le hello (JSON préfixé par sa longueur, borné ; null sur EOF ou déchet).
            //    Un hello tronqué ou déchet est une panne de transport / de protocole, pas un
            //    refus d'autorisation → HANDSHAKE_PROTOCOL_ERROR.
            var hello = await WireCodec.ReadMessageAsync<HandshakeHello>(stream, ct).ConfigureAwait(false);
            if (hello is null)
                return await DenyAsync(stream, ct, ErrorCodes.HandshakeProtocolError,
                    "No or malformed handshake hello.").ConfigureAwait(false);

            // 2. Rejeter proprement un marqueur de protocole incompatible ou déchet (panne de protocole).
            if (!string.Equals(hello.Protocol, HandshakeProtocol.Version, StringComparison.Ordinal))
                return await DenyAsync(stream, ct, ErrorCodes.HandshakeProtocolError,
                    "Unsupported handshake protocol.").ConfigureAwait(false);

            // 3. Authentifier l'appelant avant toute frappe de clé. Un pair qui n'est pas
            //    DrDxt.App est refusé ici et ne reçoit jamais de clé. C'est un refus
            //    d'autorisation → ELEVATION_DENIED (distinct d'une panne de protocole).
            var authz = _authenticator.Authorize(caller);
            if (!authz.IsOk)
                return await DenyAsync(stream, ct, ErrorCodes.ElevationDenied,
                    "Caller not authorized.").ConfigureAwait(false);

            // 4. Appelant vérifié → frapper une clé éphémère neuve et la remettre par le canal
            //    authentifié, protégé par DACL. Une panne de génération de clé est un échec interne
            //    de transport / de protocole (aucun souci d'auth) → HANDSHAKE_PROTOCOL_ERROR.
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
            // Toute panne de transport ou d'analyse → fail-closed, aucune clé. C'est un échec de
            // protocole / de transport (pas un refus d'auth) → HANDSHAKE_PROTOCOL_ERROR.
            return await DenyAsync(stream, ct, ErrorCodes.HandshakeProtocolError,
                "Handshake failed.").ConfigureAwait(false);
        }
    }

    // Envoie au mieux un Ok=false (le client doit se fermer, fail-closed) et rend Fail avec le
    // code fourni : ELEVATION_DENIED pour un refus d'auth de l'appelant, HANDSHAKE_PROTOCOL_ERROR
    // pour un handshake de transport, déchet ou tronqué. Une panne d'écriture ici est avalée : le
    // pair est peut-être déjà parti ; ce qui compte, c'est qu'aucune clé n'ait été frappée ni envoyée.
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
