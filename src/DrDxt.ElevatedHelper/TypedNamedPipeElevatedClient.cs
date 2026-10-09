using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// TypedNamedPipeElevatedClient — the M2 IElevatedClient (unelevated app side).
//
// Serializes a typed PrivilegedOp via the BOUNDED codec, signs the canonical
// (opJson, nonce) bytes with the injected session key, and sends it over the
// transport seam. Surfaces the server's fail-clean codes verbatim.
//
// SECURITY: the codec refuses any op outside the closed leaf set CLIENT-SIDE
// before anything is sent (defense in depth — the allowlist is also enforced
// server-side). The session key is INJECTED, never baked. This client lives in
// DrDxt.ElevatedHelper alongside the M0 client (plan §2 relocates the client
// abstraction to Infrastructure later); it must not pull elevated code into the
// unelevated app — only the IElevatedClient abstraction (Core) is consumed there.
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
    /// <see cref="ExecuteAsync"/> — seule la lecture de la reponse change.</summary>
    /// <summary>Le canal de retour de donnees, cote client. Meme aller-retour signe que
    /// <see cref="ExecuteAsync"/> — seule la lecture de la reponse change, et elle vit dans
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
        // Serialize via the bounded codec. A null / unknown leaf is refused here,
        // client-side, before anything touches the wire.
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
