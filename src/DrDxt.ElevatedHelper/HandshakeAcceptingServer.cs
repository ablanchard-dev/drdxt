using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Leases;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// HandshakeAcceptingServer — the elevated host's per-connection lifecycle with the
// §9.13 session-key handshake in front of the PROVEN typed protocol.
//
// Per accepted connection (deny-by-default, fail-clean throughout):
//   1. create the ACL'd, FIRST_PIPE_INSTANCE pipe (PipeSecurityFactory) and accept.
//   2. resolve the peer PID from the pipe (GetNamedPipeClientProcessId) → CallerInfo.
//   3. run HandshakeServerCore: AUTHENTICATE the caller, then mint+send an EPHEMERAL
//      per-connection key. A denied/garbage/truncated peer gets NO key and the
//      connection is dropped — the executor is never reached.
//   4. with the negotiated key, serve ONE typed request via the existing
//      TypedPrivilegedServer.ProcessTypedRequestAsync — the message protocol, HMAC,
//      nonce anti-replay, codec, leaf-exact allowlist and executor are ALL unchanged.
//      Only the key's provenance changed (negotiated, not env-var pre-shared).
//
// The session key is EPHEMERAL and per-connection (in memory only, never persisted).
// The lease watchdog (if any) is shared across connections (it lives on the helper),
// so it is injected and reused; everything else is rebuilt per connection with the
// fresh key. The same caller-auth runs again inside the typed server (defense in
// depth + lease ownership binding).
// -----------------------------------------------------------------------
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

    /// <summary>26/09/2026 — le temps laisse a un client pour parler (poignee de main, puis requete). Le tube n'a
    /// qu'une instance : sans ce delai, un programme du compte connecte et muet tenait le helper indefiniment. Il ne
    /// borne que les LECTURES, pas l'execution d'un op (un point de restauration peut etre long).</summary>
    public static readonly TimeSpan DelaiDeLectureParDefaut = TimeSpan.FromSeconds(10);

    /// <summary>Revue de sécurité du 08/10 : appelé après une poignée de main réussie seulement (SEC1 : une connexion
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

    /// <summary>Accepts exactly one connection, runs the handshake, then serves one
    /// typed request with the negotiated key. Never throws across the boundary for
    /// request-level failures; cancellation propagates.</summary>
    public async Task AcceptOneAsync(CancellationToken ct)
    {
        using var server = PipeSecurityFactory.CreateServerStream(_pipeName);
        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

        // Attribute the connection to a process so the authenticator can verify the
        // peer is the trusted DrDxt.App. A null pid → the authenticator denies.
        var caller = new CallerInfo(PipeClientIdentity.TryGetClientProcessId(server));

        // Les lectures de ce client ont un delai ; l'arret du service reste `ct`.
        using var lecture = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lecture.CancelAfter(_delaiDeLecture);

        // 3. Handshake: AUTH then mint the ephemeral key. Denied/garbage → no key.
        byte[] sessionKey;
        try
        {
            var handshake = await new HandshakeServerCore(_authenticator, _keyGenerator)
                .RunAsync(server, caller, lecture.Token)
                .ConfigureAwait(false);
            if (!handshake.IsOk || handshake.Value is null)
                return; // fail-closed: no channel, executor never reached
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
            return; // any handshake fault → drop the connection, fail-closed
        }

        // 4. Serve ONE typed request with the negotiated key over the same
        //    connection, via the proven pipeline (unchanged).
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
            // The ephemeral key dies with this connection — clear it from memory.
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
            // Peer vanished mid-response; nothing actionable.
        }
    }
}
