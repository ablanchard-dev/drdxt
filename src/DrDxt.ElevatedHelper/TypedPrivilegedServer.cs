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
// Aucune primitive générique de registre ou d'exécution. Le seul point d'entrée privilégié est
// une requête typée, signée, admise par l'allowlist ; la liaison passe par le switch de
// discriminant borné de PrivilegedOpCodec (pas de résolution par nom de type). Chaque frontière
// est emballée pour qu'aucune exception ne se propage à travers l'IPC.
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
        // Le registre de l'homme mort (dead-man's switch). Facultatif : un hôte minimal sans levier
        // tenu n'a pas à en brancher ; une op de bail échoue alors proprement (jamais de plantage).
        _leaseWatchdog = leaseWatchdog;
        // Le nom du pipe est de la config serveur (comme la clé de session), pas une entrée par appel :
        // le serveur n'expose donc aucun point d'entrée privilégié qui prenne une chaîne.
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeSecurityFactory.DefaultPipeName : pipeName;
    }

    /// <summary>
    /// Fait tourner tout le pipeline pour une requête typée décodée. Totale : rend toujours
    /// une réponse, ne lève jamais. L'exécuteur n'est appelé qu'après que la requête a passé
    /// toutes les gardes (a)..(e). <paramref name="caller"/> porte l'identité du pair qui se
    /// connecte (son process id, résolu depuis le pipe) pour que l'authentificateur (c) vérifie
    /// que c'est le DrDxt.App de confiance.
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

            // (c) Autorisation de l'appelant : couture fail-closed (vérifie que le pair est
            //     le DrDxt.App de confiance via le process id client résolu).
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
            //  * Les ops de bail gèrent le registre de l'homme mort lié à l'appelant
            //    authentifié (le propriétaire du bail) : ce ne sont pas des écritures
            //    privilégiées, elles vont donc au chien de garde, jamais à l'exécuteur. Le
            //    propriétaire est l'identité que (c) vient d'autoriser : lui seul peut renouveler ou clore.
            //  * Tout le reste est une écriture privilégiée → l'exécuteur.
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
                //  * Une vitesse fixe de ventilateur n'est posée que sous un bail du même appelant qui rend ce ventilateur à
                //    l'automatique : sans lui, ni la garde thermique (80 °C) ni l'expiration ne la verraient, et elle
                //    restait jusqu'au redémarrage pendant que la carte chauffe.
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
            // Toute erreur interne inattendue (y compris un exécuteur qui lève) →
            // réponse d'échec générique, sans fuite de détail.
            return Fail(ErrorCodes.HelperOpFailed, "Internal error processing request.");
        }
    }

    /// <summary>
    /// Accepte exactement une connexion sur le pipe nommé protégé par ACL, traite une requête
    /// typée et écrit la réponse. Ne lève jamais à travers la frontière pour un échec au
    /// niveau de la requête.
    /// </summary>
    public async Task AcceptOneAsync(CancellationToken ct)
    {
        using var server = PipeSecurityFactory.CreateServerStream(_pipeName);
        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

        // Attribuer la connexion à un processus pour que l'authentificateur vérifie que
        // le pair est le DrDxt.App de confiance. Un pid null → l'authentificateur refuse.
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

    // Le canal de retour de donnees. Le succes porte les octets en base64 ; l'echec garde son
    // code et son motif mot pour mot : « SeSystemEnvironmentPrivilege inactif » est un non concluant,
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

    // Lance une action du chien de garde (Establish/Renew). Sans chien de garde branché, l'op de
    // bail échoue proprement, jamais de plantage. L'action rend elle-même un Result : le remonter.
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

    // Le propriétaire du bail est l'identité authentifiée de l'appelant (le pid du pair résolu
    // par le pipe, déjà vérifié à l'étape (c)). Lie chaque bail à l'appelant qui le possède.
    private static LeaseOwner LeaseOwnerOf(CallerInfo caller) => new(caller.ClientProcessId);

    private static PrivilegedResponse Fail(string code, string message) =>
        new() { Ok = false, ErrorCode = code, Message = message };
}
