using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Leases;

namespace DrDxt.ElevatedHelper;

// HandshakeAcceptingServer : le cycle de vie par connexion de l'hôte élevé, le handshake de clé de session devant le
// protocole typé. Par connexion (refus par défaut, échec propre partout) :
//   1. créer le pipe FIRST_PIPE_INSTANCE protégé par ACL (PipeSecurityFactory) et accepter.
//   2. résoudre le PID du pair depuis le pipe (GetNamedPipeClientProcessId) → CallerInfo.
//   3. lancer HandshakeServerCore : authentifier l'appelant, puis frapper et envoyer une clé
//      éphémère propre à la connexion. Un pair refusé, déchet ou tronqué n'a aucune clé et la
//      connexion est coupée : l'exécuteur n'est jamais atteint.
//   4. avec la clé négociée, servir une requête typée par TypedPrivilegedServer.ProcessTypedRequestAsync (messages,
//      HMAC, anti-rejeu par nonce, codec, allowlist à la feuille près, exécuteur).
// Clé éphémère, propre à la connexion, en mémoire seulement. Le chien de garde des baux vit dans le helper : injecté et
// partagé ; le reste est reconstruit par connexion. La même auth repasse dans le serveur typé (défense en profondeur,
// bail lié à son propriétaire).
[SupportedOSPlatform("windows")]
public sealed class HandshakeAcceptingServer
{
    private readonly ICallerAuthenticator _authenticator;
    private readonly ISessionKeyGenerator _keyGenerator;
    private readonly OpAllowlist _allowlist;
    private readonly IPrivilegedOpExecutor _executor;
    private readonly LeaseWatchdog? _leaseWatchdog;
    private readonly string _pipeName;
    private readonly TimeSpan _delaiDeLecture;

    /// <summary>Le temps laisse a un client pour parler (poignee de main, puis requete). Le tube n'a
    /// qu'une instance : sans ce delai, un programme du compte connecte et muet tiendrait le helper indefiniment. Il ne
    /// borne que les lectures, pas l'execution d'un op (un point de restauration peut etre long).</summary>
    public static readonly TimeSpan DelaiDeLectureParDefaut = TimeSpan.FromSeconds(10);

    /// <summary>Appelé après une poignée de main réussie seulement (SEC1 : une connexion
    /// refusée ne garde plus le helper vivant).</summary>
    public Action<int?>? ApresAuthentification { get; init; }

    public HandshakeAcceptingServer(
        ICallerAuthenticator authenticator,
        ISessionKeyGenerator keyGenerator,
        OpAllowlist allowlist,
        IPrivilegedOpExecutor executor,
        string? pipeName = null,
        LeaseWatchdog? leaseWatchdog = null,
        TimeSpan? delaiDeLecture = null)
    {
        _delaiDeLecture = delaiDeLecture ?? DelaiDeLectureParDefaut;
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _keyGenerator = keyGenerator ?? throw new ArgumentNullException(nameof(keyGenerator));
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _leaseWatchdog = leaseWatchdog;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeSecurityFactory.DefaultPipeName : pipeName;
    }

    /// <summary>Accepte exactement une connexion, fait le handshake, puis sert une requête
    /// typée avec la clé négociée. Ne lève jamais à travers la frontière pour un échec au
    /// niveau de la requête ; l'annulation se propage.</summary>
    public async Task AcceptOneAsync(CancellationToken ct)
    {
        using var server = PipeSecurityFactory.CreateServerStream(_pipeName);
        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

        // Attribuer la connexion à un processus pour que l'authentificateur vérifie que le
        // pair est le DrDxt.App de confiance. Un pid null → l'authentificateur refuse.
        var caller = new CallerInfo(PipeClientIdentity.TryGetClientProcessId(server));

        // Les lectures de ce client ont un delai ; l'arret du service reste `ct`.
        using var lecture = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lecture.CancelAfter(_delaiDeLecture);

        // 3. Handshake : l'auth, puis la frappe de la clé éphémère. Refusé ou déchet → aucune clé.
        byte[] sessionKey;
        try
        {
            var handshake = await new HandshakeServerCore(_authenticator, _keyGenerator)
                .RunAsync(server, caller, lecture.Token)
                .ConfigureAwait(false);
            if (!handshake.IsOk || handshake.Value is null)
                return; // fail-closed : aucun canal, exécuteur jamais atteint
            sessionKey = handshake.Value;
            ApresAuthentification?.Invoke(caller.ClientProcessId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return; // client muet : la connexion expire, le service continue
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return; // toute panne de handshake → couper la connexion, fail-closed
        }

        // 4. Servir une requête typée avec la clé négociée, sur la même connexion.
        var typed = new TypedPrivilegedServer(
            sessionKey, _authenticator, _allowlist, _executor, _pipeName, _leaseWatchdog);

        PrivilegedResponse response;
        try
        {
            TypedPrivilegedRequest? request;
            try
            {
                request = await WireCodec
                    .ReadMessageAsync<TypedPrivilegedRequest>(server, lecture.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return; // la requete n'est jamais venue : la connexion expire
            }
            response = await typed.ProcessTypedRequestAsync(request, ct, caller).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            response = new PrivilegedResponse
            {
                Ok = false,
                ErrorCode = ErrorCodes.HelperOpFailed,
                Message = "Internal error processing request.",
            };
        }
        finally
        {
            // La clé éphémère meurt avec cette connexion : l'effacer de la mémoire.
            Array.Clear(sessionKey);
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
}
