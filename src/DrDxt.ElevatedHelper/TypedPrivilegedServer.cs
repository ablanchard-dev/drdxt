using System.IO.Pipes;
using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Leases;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// TypedPrivilegedServer — the M2 signed-IPC gateway for TYPED privileged ops.
//
// SECURITY pipeline, applied IN ORDER to every request (deny-by-default,
// fail-clean — never fail-open):
//   (a) HMAC verify ............ else HELPER.SIGNATURE_INVALID (not executed)
//   (b) nonce not replayed ..... else HELPER.NOT_ALLOWLISTED   (not executed)
//   (c) caller authorized ...... else ELEVATION_DENIED         (not executed)
//   (d) codec bind (bounded) ... else HELPER.NOT_ALLOWLISTED   (not executed)
//   (e) OpAllowlist.Validate ... else HELPER.NOT_ALLOWLISTED   (not executed)
//   (f) execute the allowlisted op (wrapped → HELPER.OP_FAILED on error)
//
// There is NO generic registry/exec primitive. The only privileged entry point
// is a typed, signed, allowlisted request; binding is via the bounded
// PrivilegedOpCodec discriminant switch (no type-name resolution). Every
// boundary is wrapped so an exception never propagates across the IPC.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class TypedPrivilegedServer
{
    private readonly byte[] _sessionKey;
    private readonly ICallerAuthenticator _authenticator;
    private readonly OpAllowlist _allowlist;
    private readonly IPrivilegedOpExecutor _executor;
    private readonly LeaseWatchdog? _leaseWatchdog;
    private readonly string _pipeName;
    private readonly NonceTracker _nonces = new();

    public TypedPrivilegedServer(
        byte[] sessionKey,
        ICallerAuthenticator authenticator,
        OpAllowlist allowlist,
        IPrivilegedOpExecutor executor,
        string? pipeName = null,
        LeaseWatchdog? leaseWatchdog = null)
    {
        _sessionKey = sessionKey ?? throw new ArgumentNullException(nameof(sessionKey));
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        // The dead-man's-switch registry. Optional: a minimal host with no held
        // tweaks need not wire one; a lease op then fails clean (never crashes).
        _leaseWatchdog = leaseWatchdog;
        // Pipe name is server CONFIG (like the session key), not a per-call input,
        // so the server exposes no string-taking privileged entry point.
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeSecurityFactory.DefaultPipeName : pipeName;
    }

    /// <summary>
    /// Runs the full pipeline for one decoded typed request. Total: always
    /// returns a response, never throws. The executor is invoked only after the
    /// request passes every guard (a)..(e). The <paramref name="caller"/> carries
    /// the connecting peer's identity (its process id, resolved from the pipe) so
    /// the authenticator (c) can verify it is the trusted DrDxt.App.
    /// </summary>
    public async Task<PrivilegedResponse> ProcessTypedRequestAsync(
        TypedPrivilegedRequest? request, CancellationToken ct, CallerInfo caller = default)
    {
        try
        {
            // Malformed/absent request → fail-clean.
            if (request is null)
                return Fail(ErrorCodes.NotAllowlisted, "Malformed or empty request.");

            // (a) Transport integrity: HMAC over canonical (opJson, nonce).
            if (!TypedRequestHmac.Verify(_sessionKey, request.OpJson, request.Nonce, request.HmacBase64))
                return Fail(ErrorCodes.HelperSignatureInvalid, "Request integrity check failed.");

            // (b) Anti-replay: the nonce must be fresh.
            if (!_nonces.TryRegister(request.Nonce))
                return Fail(ErrorCodes.NotAllowlisted, "Request rejected (replay or missing nonce).");

            // (c) Caller authorization: fail-closed seam (verifies the peer is
            //     the trusted DrDxt.App via the resolved client process id).
            if (!_authenticator.Authorize(caller).IsOk)
                return Fail(ErrorCodes.ElevationDenied, "Caller not authorized.");

            // (d) Bind via the BOUNDED discriminant codec (no type-name resolution).
            var decoded = PrivilegedOpCodec.Deserialize(request.OpJson);
            if (!decoded.IsOk || decoded.Value is null)
                return Fail(ErrorCodes.NotAllowlisted, "Privileged op could not be bound.");

            // (e) Leaf-exact allowlist — the real authorization (master plan §9.13).
            var allowed = _allowlist.Validate(decoded.Value);
            if (!allowed.IsOk)
                return Fail(allowed.Error!.Value.Code, allowed.Error!.Value.Message);

            // (f) Dispatch the now-known-good op.
            //  * Lease ops manage the dead-man's-switch registry tied to the
            //    AUTHENTICATED caller (the lease OWNER) — they are not privileged
            //    writes, so they go to the watchdog, never the executor. The owner is
            //    the same identity (c) just authorized: only it may renew/complete.
            //  * Everything else is a privileged write → the executor.
            return decoded.Value switch
            {
                EstablishLeaseOp el => DispatchLease(
                    () => Watchdog().Establish(LeaseOwnerOf(caller), el.LeaseId, el.RevertOpJson, el.LeaseSeconds)),
                RenewLeaseOp rl => DispatchLease(
                    () => Watchdog().Renew(LeaseOwnerOf(caller), rl.LeaseId, rl.LeaseSeconds)),

                //  * Les ops qui repondent au lieu d'agir passent par le canal de retour de
                //    donnees. Elles sont nommees une par une ici : c'est l'unique endroit qui
                //    decide qu'une op rend des octets, et une op non nommee ne peut donc pas
                //    en rendre par accident. Elles ont subi exactement les memes gardes (a)..(e).
                ReadFirmwareVariableOp => await ExecuteReadAsync(decoded.Value, ct).ConfigureAwait(false),
                ReadComponentStoreHealthOp => await ExecuteReadAsync(decoded.Value, ct).ConfigureAwait(false),

                _ => await ExecutePrivilegedAsync(decoded.Value, ct).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a request failure — propagate
        }
        catch
        {
            // Any unexpected internal error (incl. a throwing executor) →
            // generic fail response, no detail leak.
            return Fail(ErrorCodes.HelperOpFailed, "Internal error processing request.");
        }
    }

    /// <summary>
    /// Accepts exactly one connection over the ACL'd named pipe, processes one
    /// typed request, and writes the response. Never throws across the boundary
    /// for request-level failures.
    /// </summary>
    public async Task AcceptOneAsync(CancellationToken ct)
    {
        using var server = PipeSecurityFactory.CreateServerStream(_pipeName);
        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

        // Attribute the connection to a process so the authenticator can verify
        // the peer is the trusted DrDxt.App. A null pid → the authenticator denies.
        var caller = new CallerInfo(PipeClientIdentity.TryGetClientProcessId(server));

        PrivilegedResponse response;
        try
        {
            var request = await WireCodec
                .ReadMessageAsync<TypedPrivilegedRequest>(server, ct)
                .ConfigureAwait(false);
            response = await ProcessTypedRequestAsync(request, ct, caller).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            response = Fail(ErrorCodes.HelperOpFailed, "Internal error processing request.");
        }

        try
        {
            await WireCodec.WriteMessageAsync(server, response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Peer vanished mid-response; nothing actionable.
        }
    }

    private async Task<PrivilegedResponse> ExecutePrivilegedAsync(PrivilegedOp op, CancellationToken ct)
    {
        var exec = await _executor.ExecuteAsync(op, ct).ConfigureAwait(false);
        return exec.IsOk
            ? new PrivilegedResponse { Ok = true }
            : Fail(exec.Error?.Code ?? ErrorCodes.HelperOpFailed,
                   exec.Error?.Message ?? "Privileged operation failed.");
    }

    // Le canal de retour de donnees. Le succes porte les octets en base64 ; l'echec garde son
    // code et son motif verbatim -- « SeSystemEnvironmentPrivilege inactif » est un NON CONCLUANT,
    // et l'ecraser en un HELPER.OP_FAILED generique en ferait un refus du firmware.
    private async Task<PrivilegedResponse> ExecuteReadAsync(PrivilegedOp op, CancellationToken ct)
    {
        var read = await _executor.ReadAsync(op, ct).ConfigureAwait(false);

        if (!read.IsOk)
            return Fail(read.Error?.Code ?? ErrorCodes.HelperOpFailed,
                        read.Error?.Message ?? "Privileged read failed.");

        // Un succes sans octets n'est pas une lecture : le dire, plutot que rendre un Ok vide
        // que l'appelant lirait comme « la variable est vide ».
        var data = read.Value;
        if (data is null)
            return Fail(ErrorCodes.HelperOpFailed, "The read succeeded but returned no data.");

        // Depassement = echec, jamais troncature : la moitie d'une variable firmware serait lue
        // comme une valeur entiere par tout ce qui vient apres.
        if (data.Length > PrivilegedResponse.MaxDataBytes)
            return Fail(ErrorCodes.HelperOpFailed,
                        $"The read returned more than {PrivilegedResponse.MaxDataBytes} bytes.");

        return new PrivilegedResponse { Ok = true, DataBase64 = Convert.ToBase64String(data) };
    }

    // Runs a watchdog action (Establish/Renew). If no watchdog is wired, the lease
    // op fails clean — never a crash. The action itself returns a Result; surface it.
    private PrivilegedResponse DispatchLease(Func<Result> action)
    {
        if (_leaseWatchdog is null)
            return Fail(ErrorCodes.HelperOpFailed, "Lease watchdog is not available on this helper.");
        var r = action();
        return r.IsOk
            ? new PrivilegedResponse { Ok = true }
            : Fail(r.Error?.Code ?? ErrorCodes.HelperOpFailed, r.Error?.Message ?? "Lease operation failed.");
    }

    private LeaseWatchdog Watchdog() => _leaseWatchdog!; // guarded by DispatchLease

    // The lease OWNER is the authenticated caller identity (the pipe-resolved peer
    // pid already verified in step (c)). Ties every lease to the caller that owns it.
    private static LeaseOwner LeaseOwnerOf(CallerInfo caller) => new(caller.ClientProcessId);

    private static PrivilegedResponse Fail(string code, string message) =>
        new() { Ok = false, ErrorCode = code, Message = message };
}
