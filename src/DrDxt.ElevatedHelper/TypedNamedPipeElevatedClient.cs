using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// TypedNamedPipeElevatedClient : l'IElevatedClient (côté app non élevée).
//
// Sérialise une PrivilegedOp typée par le codec borné, signe les octets canoniques
// (opJson, nonce) avec la clé de session injectée et les envoie par la couture de transport.
// Remonte mot pour mot les codes d'échec propre du serveur.
//
// Sécurité : le codec refuse côté client toute op hors de l'ensemble fermé de feuilles avant
// tout envoi (défense en profondeur : l'allowlist est aussi imposée côté serveur). La clé de
// session est injectée, jamais codée en dur. Ce client vit dans DrDxt.ElevatedHelper à côté du
// premier client (l'abstraction client pourrait passer plus tard dans Infrastructure) ; il ne doit
// pas tirer de code élevé dans l'app non élevée : seule l'abstraction IElevatedClient (Core) y
// est consommée.
// -----------------------------------------------------------------------
public sealed class TypedNamedPipeElevatedClient : IElevatedClient
{
    private readonly byte[] _sessionKey;
    private readonly ITypedRequestTransport _transport;

    public TypedNamedPipeElevatedClient(byte[] sessionKey, ITypedRequestTransport transport)
    {
        _sessionKey = sessionKey ?? throw new ArgumentNullException(nameof(sessionKey));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
    {
        var round = await RoundTripAsync(op, ct).ConfigureAwait(false);
        if (!round.IsOk || round.Value is null)
            return Result.Fail(round.Error!.Value.Code, round.Error.Value.Message);

        var response = round.Value;
        return response.Ok
            ? Result.Ok()
            : Result.Fail(response.ErrorCode ?? ErrorCodes.IoFailure,
                          response.Message ?? "Privileged operation failed.");
    }

    /// <summary>Le canal de retour de donnees, cote client. Meme aller-retour signe que
    /// <see cref="ExecuteAsync"/> : seule la lecture de la reponse change.</summary>
    /// <summary>Le canal de retour de donnees, cote client. Meme aller-retour signe que
    /// <see cref="ExecuteAsync"/> : seule la lecture de la reponse change, et elle vit dans
    /// PrivilegedResponse.ToBytes() pour que les deux clients partagent la meme regle.</summary>
    public async Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
    {
        var round = await RoundTripAsync(op, ct).ConfigureAwait(false);
        return round.IsOk && round.Value is not null
            ? round.Value.ToBytes()
            : Result<byte[]>.Fail(round.Error!.Value.Code, round.Error.Value.Message);
    }

    // L'aller-retour commun : serialisation bornee, signature, envoi. Un seul endroit signe.
    private async Task<Result<PrivilegedResponse>> RoundTripAsync(PrivilegedOp op, CancellationToken ct)
    {
        // Sérialiser par le codec borné. Une feuille null ou inconnue est refusée ici,
        // côté client, avant que quoi que ce soit parte sur le fil.
        var serialized = PrivilegedOpCodec.Serialize(op);
        if (!serialized.IsOk || serialized.Value is null)
            return Result<PrivilegedResponse>.Fail(ErrorCodes.NotAllowlisted, "Unsupported privileged operation.");

        var opJson = serialized.Value;
        var nonce = Guid.NewGuid().ToString("N");
        var hmac = TypedRequestHmac.Compute(_sessionKey, opJson, nonce);
        var request = new TypedPrivilegedRequest { OpJson = opJson, Nonce = nonce, HmacBase64 = hmac };

        PrivilegedResponse response;
        try
        {
            response = await _transport.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result<PrivilegedResponse>.Fail(ErrorCodes.IoFailure, "Could not reach the elevated helper.");
        }

        return response is null
            ? Result<PrivilegedResponse>.Fail(ErrorCodes.IoFailure, "No response from elevated helper.")
            : Result<PrivilegedResponse>.Ok(response);
    }
}
