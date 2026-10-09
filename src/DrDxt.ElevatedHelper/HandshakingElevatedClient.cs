using System.IO.Pipes;
using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// HandshakingElevatedClient — the App-side IElevatedClient that negotiates the
// session key over the pipe (§9.13) instead of reading a pre-shared env-var key.
//
// Per op (one connection): connect → HANDSHAKE (get the ephemeral key the verified
// server minted for us) → serialize the op via the BOUNDED codec → HMAC the
// canonical (opJson, nonce) with the NEGOTIATED key → send the typed request →
// read the response. Codec / HMAC / wire framing are the PROVEN pieces, untouched;
// only the key's provenance is new.
//
// Anti-replay is preserved: a fresh nonce per request, and the ephemeral key is
// per-connection. Every transport fault is wrapped → a fail-clean Result; an
// exception never escapes (the App falls back to UnavailableElevatedClient).
//
// Authentification du serveur — posee le 11/09/2026, elle ferme le defaut D.3 de la revue.
// Elle est SYMETRIQUE de celle du serveur : celui-ci resout le pid de l'appelant depuis le
// tube (GetNamedPipeClientProcessId → chemin + SHA-256, HandshakeServerCore etape 3) ; le
// client resout maintenant le pid du SERVEUR (GetNamedPipeServerProcessId) et compare son
// image au chemin attendu du helper. Sans elle, le nom du tube etant une chaine fixe et
// publique, un processus non privilegie tournant sous le meme compte pouvait le prendre
// Avant le helper (qui, lui, le detecte : FirstPipeInstance echoue) puis repondre Ok a
// tout : l'App croyait qu'un op privilegie avait ete applique alors que rien ne l'avait
// ete, et le squatteur recevait en prime la liste des ops demandees. Ce n'etait pas une
// elevation — le squatteur tourne deja sous le compte de l'utilisateur — c'etait un
// MENSONGE rendu au client, ce qui pour ce produit est la faute grave.
//
// La garde est FAIL-CLOSED et NOMMEE : sans chemin attendu, sans pid resoluble, sans image
// lisible, ou sur un chemin qui differe, la session est refusee avant le handshake avec un
// message qui dit lequel des quatre cas s'est produit. Le refus et le cas legitime sont
// tous deux mesures en in-process (TubeUsurpeTests / HandshakeChannelIntegrationTests) —
// une garde dont seul le refus serait prouve pourrait refuser tout, toujours, sous une
// suite verte.
//
// La traversee d'integrite reelle (App moyenne → helper ELEVE) est mesuree depuis le 11/09/2026
// au soir (revue de securite interne, non publiee, « GATE FERME ») : `tools/helperhandshake` lance a
// integrite moyenne (0x2000, lu par GetTokenInformation sur le processus vivant), helper eleve
// par UAC, garde du serveur ACTIVE → OVERALL PASS, cle negociee, deux Ping. Les tests, eux,
// tournent toujours a integrite egale : cette preuve-la est un banc manuel, pas un test. Le choix
// de PROCESS_QUERY_LIMITED_INFORMATION plutot que de Process.MainModule est fait pour ce cas
// precis (voir PipeServerIdentity).
//
// LIMITE ASSUMEE : la comparaison porte sur le chemin de l'image, pas sur son empreinte ni
// sur son niveau d'integrite. Un attaquant qui lancerait le vrai helper non eleve depuis le
// Vrai chemin passerait la garde — mais il echouerait alors sur les ops privilegiees, donc
// il rendrait un echec honnete, pas un faux « applique ». Et l'injection intra-utilisateur
// bat de toute facon toute verification d'image (risque connu et accepte, D.4).
//
// FIX 1 — startup-latency absorption lives here, in the real op's connect: a
// bounded connect RETRY loop (connect failures retried every ~pollIntervalMs up to
// connectTimeoutMs) so that the very first op after an on-demand launch reaches the
// just-spawned helper without a separate liveness probe consuming an accept. The
// handshake itself is attempted ONCE per successful connect (not retried — a
// rejection is terminal), so there is exactly one accept for the op.
//
// NOTE (plan §2): the client abstraction is co-located here for now; M5 relocates
// it to DrDxt.Infrastructure ("élévation client") without changing the channel.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class HandshakingElevatedClient : IElevatedClient
{
    private readonly string _pipeName;
    private readonly string? _cheminHelperAttendu;
    private readonly int _connectTimeoutMs;
    private readonly int _connectRetryIntervalMs;

    /// <param name="expectedServerImagePath">Le chemin complet de l'image du helper que ce
    /// client accepte d'ecouter. OBLIGATOIRE de fait : un null (ou un chemin vide) ne
    /// desactive pas la garde, il la fait REFUSER — aucun appelant ne peut donc obtenir un
    /// canal non authentifie en omettant l'argument. Les deux fabriques de production le
    /// resolvent par <see cref="PipeServerIdentity.TryResolveExpectedHelperPath"/>.</param>
    public HandshakingElevatedClient(
        string? pipeName = null,
        string? expectedServerImagePath = null,
        int connectTimeoutMs = 5000,
        int connectRetryIntervalMs = 150)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeSecurityFactory.DefaultPipeName : pipeName;
        _cheminHelperAttendu = string.IsNullOrWhiteSpace(expectedServerImagePath) ? null : expectedServerImagePath;
        _connectTimeoutMs = connectTimeoutMs;
        _connectRetryIntervalMs = Math.Max(10, connectRetryIntervalMs);
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

    /// <summary>Le canal de retour de donnees sur le chemin de PRODUCTION (celui que l'app
    /// utilise reellement). Sans cette surcharge, une lecture partie de l'app serait tombee
    /// sur le refus par defaut de IElevatedClient : un echec honnete, mais qui aurait fait
    /// croire que le canal n'existe pas alors qu'il est cable partout ailleurs.</summary>
    public async Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
    {
        var round = await RoundTripAsync(op, ct).ConfigureAwait(false);
        return round.IsOk && round.Value is not null
            ? round.Value.ToBytes()
            : Result<byte[]>.Fail(round.Error!.Value.Code, round.Error.Value.Message);
    }

    // L'aller-retour complet : connect + handshake + requete signee + reponse. Un seul endroit
    // ouvre le tuyau et signe ; ExecuteAsync et ReadAsync ne different que par la lecture de
    // la reponse. La cle ephemere est effacee ici quoi qu'il arrive.
    private async Task<Result<PrivilegedResponse>> RoundTripAsync(PrivilegedOp op, CancellationToken ct)
    {
        // Serialize via the bounded codec first — refuse an unknown leaf client-side
        // before anything touches the wire (defense in depth; the server re-checks).
        var serialized = PrivilegedOpCodec.Serialize(op);
        if (!serialized.IsOk || serialized.Value is null)
            return Result<PrivilegedResponse>.Fail(ErrorCodes.NotAllowlisted, "Unsupported privileged operation.");
        var opJson = serialized.Value;

        byte[]? sessionKey = null;
        try
        {
            using var client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            // Bounded connect RETRY: a just-launched helper may not be serving its
            // FirstPipeInstance pipe for a few hundred ms. Retry connect failures up
            // to the overall deadline, then fail clean as HELPER_UNREACHABLE.
            if (!await TryConnectWithRetryAsync(client, ct).ConfigureAwait(false))
                return Result<PrivilegedResponse>.Fail(ErrorCodes.HelperUnreachable,
                    "Could not reach the elevated helper (it did not start serving in time).");

            // Authentification du serveur, avant le handshake : on ne dit rien a un tube dont
            // on ne sait pas qui le sert. Poser la garde ici et pas apres n'est pas cosmetique —
            // le handshake livre au pair la liste des ops que DrDXT veut appliquer.
            var authentification = AuthentifierLeServeur(client);
            if (!authentification.IsOk)
                return Result<PrivilegedResponse>.Fail(
                    authentification.Error!.Value.Code, authentification.Error.Value.Message);

            // Handshake: obtain the ephemeral session key the AUTHENTICATED server minted.
            var handshake = await HandshakeClientCore.RunAsync(client, ct).ConfigureAwait(false);
            if (!handshake.IsOk || handshake.Value is null)
                return Result<PrivilegedResponse>.Fail(
                    handshake.Error?.Code ?? ErrorCodes.ElevationDenied,
                    handshake.Error?.Message ?? "Handshake with the elevated helper failed.");
            sessionKey = handshake.Value;

            // Sign + send the typed request with the NEGOTIATED key (proven protocol).
            var nonce = Guid.NewGuid().ToString("N");
            var hmac = TypedRequestHmac.Compute(sessionKey, opJson, nonce);
            var request = new TypedPrivilegedRequest { OpJson = opJson, Nonce = nonce, HmacBase64 = hmac };

            await WireCodec.WriteMessageAsync(client, request, ct).ConfigureAwait(false);
            var response = await WireCodec
                .ReadMessageAsync<PrivilegedResponse>(client, ct)
                .ConfigureAwait(false);

            return response is null
                ? Result<PrivilegedResponse>.Fail(ErrorCodes.IoFailure, "No response from elevated helper.")
                : Result<PrivilegedResponse>.Ok(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return Result<PrivilegedResponse>.Fail(ErrorCodes.IoFailure, "Could not reach the elevated helper.");
        }
        finally
        {
            if (sessionKey is not null)
                Array.Clear(sessionKey); // ephemeral key dies with the connection
        }
    }

    // La garde. Quatre refus NOMMES, aucun silence, aucun repli : « pas de chemin attendu »,
    // « processus introuvable », « image illisible » et « mauvaise image » se distinguent dans
    // le message, parce qu'ils ne se corrigent pas pareil. Un seul chemin rend Ok : celui ou
    // l'image du serveur est le helper attendu.
    private Result AuthentifierLeServeur(NamedPipeClientStream client)
    {
        if (_cheminHelperAttendu is null)
            return Refus("serveur du tube non authentifié : aucun chemin de helper attendu n'a été fourni au client.");

        var image = PipeServerIdentity.TryGetServerImagePath(client);
        if (string.IsNullOrWhiteSpace(image))
            return Refus("serveur du tube non authentifié : le processus qui sert le tube est introuvable.");

        string attendu, trouve;
        try
        {
            attendu = Path.GetFullPath(_cheminHelperAttendu);
            trouve = Path.GetFullPath(image);
        }
        catch
        {
            return Refus("serveur du tube non authentifié : le chemin de son image est illisible.");
        }

        return string.Equals(attendu, trouve, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok()
            : Refus($"serveur du tube non authentifié : processus attendu {attendu}, trouvé {trouve}.");
    }

    private static Result Refus(string pourquoi) => Result.Fail(ErrorCodes.ElevationDenied, pourquoi);

    // Connects to the helper pipe, retrying CONNECT failures (the helper may still
    // be starting after an on-demand launch) every _connectRetryIntervalMs until the
    // overall _connectTimeoutMs deadline. Returns true on a connected stream; false
    // if the deadline passed without a connection. Cancellation propagates.
    private async Task<bool> TryConnectWithRetryAsync(NamedPipeClientStream client, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, _connectTimeoutMs);
        while (true)
        {
            var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
            // Cap each connect attempt at the retry interval (or the remaining budget)
            // so a single ConnectAsync does not block the whole deadline at once.
            var attemptTimeout = Math.Min(_connectRetryIntervalMs, Math.Max(1, remaining));
            try
            {
                await client.ConnectAsync(attemptTimeout, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                // Pipe not up yet → retry until the deadline.
                if (Environment.TickCount64 >= deadline)
                    return false;
            }
        }
    }
}
