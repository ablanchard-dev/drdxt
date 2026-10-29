using System.IO.Pipes;
using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Leases;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// TypedPrivilegedServer : la passerelle IPC signée pour les ops privilégiées typées.
//
// Pipeline de sécurité, appliqué dans l'ordre à chaque requête (refus par défaut,
// échec propre, jamais fail-open) :
//   (a) vérification HMAC ........ sinon HELPER.SIGNATURE_INVALID (non exécutée)
//   (b) nonce non rejoué ......... sinon HELPER.NOT_ALLOWLISTED   (non exécutée)
//   (c) appelant autorisé ........ sinon ELEVATION_DENIED         (non exécutée)
//   (d) liaison codec (bornée) ... sinon HELPER.NOT_ALLOWLISTED   (non exécutée)
//   (e) OpAllowlist.Validate ..... sinon HELPER.NOT_ALLOWLISTED   (non exécutée)
//   (f) exécution de l'op admise (emballée → HELPER.OP_FAILED en cas d'erreur)
//
// Aucune primitive générique de registre ou d'exécution : une requête typée, signée, admise par l'allowlist, liée par le
// discriminant borné de PrivilegedOpCodec (pas de résolution par nom de type). Aucune exception ne traverse l'IPC.
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
        // L'homme mort, facultatif : sans lui, une op de bail échoue proprement.
        _leaseWatchdog = leaseWatchdog;
        // Le nom du pipe est de la config serveur, comme la clé de session : aucun point d'entrée privilégié ne prend une
        // chaîne.
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeSecurityFactory.DefaultPipeName : pipeName;
    }

    /// <summary>
    /// Tout le pipeline pour une requête typée décodée ; rend toujours une réponse, ne lève jamais. L'exécuteur n'est
    /// appelé qu'après les gardes (a)..(e). <paramref name="caller"/> porte le pid du pair, résolu par le pipe, que (c)
    /// vérifie.
    /// </summary>
    public async Task<PrivilegedResponse> ProcessTypedRequestAsync(
        TypedPrivilegedRequest? request, CancellationToken ct, CallerInfo caller = default)
    {
        try
        {
            // Requête malformée ou absente → échec propre.
            if (request is null)
                return Fail(ErrorCodes.NotAllowlisted, "Malformed or empty request.");

            // (a) Intégrité du transport : HMAC sur le couple canonique (opJson, nonce).
            if (!TypedRequestHmac.Verify(_sessionKey, request.OpJson, request.Nonce, request.HmacBase64))
                return Fail(ErrorCodes.HelperSignatureInvalid, "Request integrity check failed.");

            // (b) Anti-rejeu : le nonce doit être neuf.
            if (!_nonces.TryRegister(request.Nonce))
                return Fail(ErrorCodes.NotAllowlisted, "Request rejected (replay or missing nonce).");

            // (c) Autorisation de l'appelant, fail-closed : le pair est-il le DrDxt.App de confiance ?
            if (!_authenticator.Authorize(caller).IsOk)
                return Fail(ErrorCodes.ElevationDenied, "Caller not authorized.");

            // (d) Liaison par le codec de discriminant borné (pas de résolution par nom de type).
            var decoded = PrivilegedOpCodec.Deserialize(request.OpJson);
            if (!decoded.IsOk || decoded.Value is null)
                return Fail(ErrorCodes.NotAllowlisted, "Privileged op could not be bound.");

            // (e) Allowlist à la feuille près : la vraie autorisation.
            var allowed = _allowlist.Validate(decoded.Value);
            if (!allowed.IsOk)
                return Fail(allowed.Error!.Value.Code, allowed.Error!.Value.Message);

            // (f) Aiguiller l'op désormais reconnue bonne.
            //  * Les ops de bail vont au chien de garde, jamais à l'exécuteur, au nom de l'identité que (c) vient
            //    d'autoriser : lui seul renouvelle ou clôt.
            //  * Tout le reste est une écriture privilégiée → l'exécuteur.
            return decoded.Value switch
            {
                EstablishLeaseOp el => DispatchLease(
                    () => Watchdog().Establish(LeaseOwnerOf(caller), el.LeaseId, el.RevertOpJson, el.LeaseSeconds)),
                RenewLeaseOp rl => DispatchLease(
                    () => Watchdog().Renew(LeaseOwnerOf(caller), rl.LeaseId, rl.LeaseSeconds)),

                //  * Les ops qui repondent au lieu d'agir passent par le canal de retour de donnees, nommees une par une :
                //    le seul endroit qui decide qu'une op rend des octets. Memes gardes (a)..(e).
                //  * Une vitesse fixe de ventilateur seulement sous un bail du même appelant qui la rend à l'automatique :
                //    sinon ni la garde thermique (80 °C) ni l'expiration ne la verraient, jusqu'au redémarrage.
                ApplyGpuFanCurveOp f when _leaseWatchdog?.VentilateurSousBail(LeaseOwnerOf(caller), f.GpuIndex) != true
                    => Fail(ErrorCodes.NotAllowlisted, "A fixed fan speed needs an active lease that gives the fan back to the card."),

                ReadFirmwareVariableOp => await ExecuteReadAsync(decoded.Value, ct).ConfigureAwait(false),
                ReadComponentStoreHealthOp => await ExecuteReadAsync(decoded.Value, ct).ConfigureAwait(false),

                _ => await ExecutePrivilegedAsync(decoded.Value, ct, caller).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            throw; // une annulation n'est pas un échec de requête : propager
        }
        catch
        {
            // Erreur interne inattendue (exécuteur qui lève compris) → échec générique, sans détail.
            return Fail(ErrorCodes.HelperOpFailed, "Internal error processing request.");
        }
    }

    /// <summary>
    /// Accepte une connexion sur le pipe protégé par ACL, traite une requête typée et répond. Un échec de requête ne lève
    /// jamais à travers la frontière.
    /// </summary>
    public async Task AcceptOneAsync(CancellationToken ct)
    {
        using var server = PipeSecurityFactory.CreateServerStream(_pipeName);
        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

        // Le pid du pair, pour l'authentificateur ; null → refus.
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
            // Le pair a disparu en pleine réponse ; rien à faire.
        }
    }

    private async Task<PrivilegedResponse> ExecutePrivilegedAsync(PrivilegedOp op, CancellationToken ct, CallerInfo caller)
    {
        var exec = await _executor.ExecuteAsync(op, ct, caller).ConfigureAwait(false);
        return exec.IsOk
            ? new PrivilegedResponse { Ok = true }
            : Fail(exec.Error?.Code ?? ErrorCodes.HelperOpFailed,
                   exec.Error?.Message ?? "Privileged operation failed.");
    }

    // Le canal de retour de donnees : octets en base64, ou l'echec avec son code et son motif mot pour mot
    // (« SeSystemEnvironmentPrivilege inactif » est un non concluant, pas un refus du firmware).
    private async Task<PrivilegedResponse> ExecuteReadAsync(PrivilegedOp op, CancellationToken ct)
    {
        var read = await _executor.ReadAsync(op, ct).ConfigureAwait(false);

        if (!read.IsOk)
            return Fail(read.Error?.Code ?? ErrorCodes.HelperOpFailed,
                        read.Error?.Message ?? "Privileged read failed.");

        // Un succes sans octets n'est pas une lecture (pas un Ok vide lu comme « variable vide »).
        var data = read.Value;
        if (data is null)
            return Fail(ErrorCodes.HelperOpFailed, "The read succeeded but returned no data.");

        // Depassement = echec, jamais troncature : une demi-variable firmware passerait pour entiere.
        if (data.Length > PrivilegedResponse.MaxDataBytes)
            return Fail(ErrorCodes.HelperOpFailed,
                        $"The read returned more than {PrivilegedResponse.MaxDataBytes} bytes.");

        return new PrivilegedResponse { Ok = true, DataBase64 = Convert.ToBase64String(data) };
    }

    // Une action du chien de garde (Establish/Renew), son Result remonté ; sans chien de garde, échec propre.
    private PrivilegedResponse DispatchLease(Func<Result> action)
    {
        if (_leaseWatchdog is null)
            return Fail(ErrorCodes.HelperOpFailed, "Lease watchdog is not available on this helper.");
        var r = action();
        return r.IsOk
            ? new PrivilegedResponse { Ok = true }
            : Fail(r.Error?.Code ?? ErrorCodes.HelperOpFailed, r.Error?.Message ?? "Lease operation failed.");
    }

    private LeaseWatchdog Watchdog() => _leaseWatchdog!; // gardé par DispatchLease

    // Le propriétaire du bail : le pid du pair, vérifié à l'étape (c).
    private static LeaseOwner LeaseOwnerOf(CallerInfo caller) => new(caller.ClientProcessId);

    private static PrivilegedResponse Fail(string code, string message) =>
        new() { Ok = false, ErrorCode = code, Message = message };
}
