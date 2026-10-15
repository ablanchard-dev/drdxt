using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// HandshakeClientCore : le bout App (non élevé) du handshake de clé de session.
//
// Travaille sur un Stream duplex brut : il se teste avec une paire de flux en mémoire.
// Un aller-retour :
//   1. envoyer HandshakeHello (marqueur de protocole ; aucun secret)
//   2. lire HandshakeResult
//   3. n'accepter qu'un résultat Ok=true bien formé qui porte une clé de 32 octets ; tout le
//      reste (Ok=false, clé absente, courte ou déchet, réponse tronquée) -> échec propre,
//      aucune clé.
//
// La clé rendue alimente le TypedNamedPipeElevatedClient existant : le protocole de messages
// typé / HMAC éprouvé ne change pas ; seule la provenance de la clé change.
// -----------------------------------------------------------------------
public static class HandshakeClientCore
{
    public static async Task<Result<byte[]>> RunAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            // 1. Saluer le serveur avec le seul marqueur de protocole.
            await WireCodec.WriteMessageAsync(stream, new HandshakeHello(), ct).ConfigureAwait(false);

            // 2. Lire l'issue (borné ; null sur EOF, déchet ou taille démesurée).
            var result = await WireCodec.ReadMessageAsync<HandshakeResult>(stream, ct).ConfigureAwait(false);
            if (result is null)
                return Fail("No handshake response from the elevated helper.");

            // 3. Fail-closed sauf si le serveur nous a explicitement autorisés avec une clé.
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
