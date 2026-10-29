using System.IO.Pipes;
using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;

namespace DrDxt.ElevatedHelper;

// HandshakingElevatedClient : l'IElevatedClient côté App qui négocie la clé de session sur le pipe. Par op, une
// connexion : handshake (la clé éphémère frappée par le serveur vérifié) → op sérialisée par le codec borné → HMAC de
// (opJson, nonce) → requête typée → réponse. Anti-rejeu : un nonce neuf par requête, une clé propre à la connexion.
// Toute panne de transport → un Result en échec ; aucune exception ne s'échappe (repli UnavailableElevatedClient).
//
// Authentification du serveur (defaut D.3), symetrique de celle du serveur (GetNamedPipeClientProcessId → chemin +
// SHA-256, HandshakeServerCore etape 3) : le client resout le pid du serveur (GetNamedPipeServerProcessId) et compare
// son image au chemin attendu du helper. Le nom du tube est public : un processus non privilegie du compte pourrait le
// prendre avant le helper (qui le detecte : FirstPipeInstance echoue), repondre Ok a tout et recevoir la liste des ops.
// Fail-closed et nommee : sans chemin attendu, sans pid, sans image lisible, ou sur un autre chemin, refus avant le
// handshake, avec le cas dans le message. Refus et cas legitime mesures en in-process (TubeUsurpeTests /
// HandshakeChannelIntegrationTests). La traversee d'integrite reelle (App moyenne 0x2000, lue par GetTokenInformation →
// helper eleve par UAC → OVERALL PASS, cle negociee, deux Ping) est un banc manuel, `tools/helperhandshake` : d'ou
// PROCESS_QUERY_LIMITED_INFORMATION plutot que Process.MainModule (voir PipeServerIdentity).
// Apres l'image, le privilege (PipeServerIdentity.RefusDePrivilege) : tube aux Administrateurs ou a SYSTEM, jeton du
// serveur eleve s'il se lit ; sinon le vrai helper lance non eleve depuis le vrai chemin passerait. L'injection
// intra-utilisateur bat toute verification d'image (risque connu et accepte, D.4).
//
// La latence de démarrage s'absorbe dans la connexion de la vraie op (reprises toutes les ~pollIntervalMs jusqu'à
// connectTimeoutMs), sans sonde de vie qui consommerait une acceptation ; le handshake n'est tenté qu'une fois (un rejet
// est définitif) : une acceptation par op.
[SupportedOSPlatform("windows")]
public sealed class HandshakingElevatedClient : IElevatedClient
{
    private readonly string _pipeName;
    private readonly string? _cheminHelperAttendu;
    private readonly int _connectTimeoutMs;
    private readonly int _connectRetryIntervalMs;

    /// <param name="expectedServerImagePath">Le chemin complet de l'image du helper que ce client accepte d'ecouter. Null
    /// ou vide fait refuser la garde, jamais la desactive. Les deux fabriques de production le resolvent par
    /// <see cref="PipeServerIdentity.TryResolveExpectedHelperPath"/>.</param>
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

    /// <summary>Le canal de retour de donnees sur le chemin de production ; sans cette surcharge, une lecture de l'app
    /// tomberait sur le refus par defaut de IElevatedClient.</summary>
    public async Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
    {
        var round = await RoundTripAsync(op, ct).ConfigureAwait(false);
        return round.IsOk && round.Value is not null
            ? round.Value.ToBytes()
            : Result<byte[]>.Fail(round.Error!.Value.Code, round.Error.Value.Message);
    }

    // L'aller-retour complet, seul endroit qui ouvre le tuyau et signe (ExecuteAsync et ReadAsync ne different que par la
    // lecture de la reponse). La cle ephemere est effacee ici quoi qu'il arrive.
    private async Task<Result<PrivilegedResponse>> RoundTripAsync(PrivilegedOp op, CancellationToken ct)
    {
        // Sérialiser d'abord par le codec borné : refuser une feuille inconnue côté client
        // avant que quoi que ce soit parte sur le fil (défense en profondeur ; le serveur revérifie).
        var serialized = PrivilegedOpCodec.Serialize(op);
        if (!serialized.IsOk || serialized.Value is null)
            return Result<PrivilegedResponse>.Fail(ErrorCodes.NotAllowlisted, "Unsupported privileged operation.");
        var opJson = serialized.Value;

        byte[]? sessionKey = null;
        // Une fois la requête écrite, une panne du tube ne dit plus « rien n'est parti » : l'op a pu s'exécuter.
        var envoyee = false;
        try
        {
            using var client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            // Un helper juste lancé peut ne pas servir son pipe quelques centaines de ms : reprises jusqu'à l'échéance, puis
            // HELPER_UNREACHABLE.
            if (!await TryConnectWithRetryAsync(client, ct).ConfigureAwait(false))
                return Result<PrivilegedResponse>.Fail(ErrorCodes.HelperUnreachable,
                    "Could not reach the elevated helper (it did not start serving in time).");

            // Authentification du serveur avant le handshake, qui livre au pair la liste des ops que DrDXT veut appliquer.
            var authentification = AuthentifierLeServeur(client);
            if (!authentification.IsOk)
                return Result<PrivilegedResponse>.Fail(
                    authentification.Error!.Value.Code, authentification.Error.Value.Message);

            // Handshake : obtenir la clé de session éphémère frappée par le serveur authentifié.
            var handshake = await HandshakeClientCore.RunAsync(client, ct).ConfigureAwait(false);
            if (!handshake.IsOk || handshake.Value is null)
                return Result<PrivilegedResponse>.Fail(
                    handshake.Error?.Code ?? ErrorCodes.ElevationDenied,
                    handshake.Error?.Message ?? "Handshake with the elevated helper failed.");
            sessionKey = handshake.Value;

            // Signer + envoyer la requête typée avec la clé négociée (protocole éprouvé).
            var nonce = Guid.NewGuid().ToString("N");
            var hmac = TypedRequestHmac.Compute(sessionKey, opJson, nonce);
            var request = new TypedPrivilegedRequest { OpJson = opJson, Nonce = nonce, HmacBase64 = hmac };

            await WireCodec.WriteMessageAsync(client, request, ct).ConfigureAwait(false);
            envoyee = true;
            var response = await WireCodec
                .ReadMessageAsync<PrivilegedResponse>(client, ct)
                .ConfigureAwait(false);

            return response is null
                ? Result<PrivilegedResponse>.Fail(ErrorCodes.HelperNoReply, "The request was sent, then the elevated helper stopped answering.")
                : Result<PrivilegedResponse>.Ok(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return envoyee
                ? Result<PrivilegedResponse>.Fail(ErrorCodes.HelperNoReply, "The request was sent, then the elevated helper stopped answering.")
                : Result<PrivilegedResponse>.Fail(ErrorCodes.IoFailure, "Could not reach the elevated helper.");
        }
        finally
        {
            if (sessionKey is not null)
                Array.Clear(sessionKey); // la clé éphémère meurt avec la connexion
        }
    }

    // La garde, des refus nommes sans repli : « pas de chemin attendu », « processus introuvable », « image illisible »,
    // « mauvaise image », puis le privilege (proprietaire du tube, jeton non eleve) : ils ne se corrigent pas pareil.
    // Seul Ok : l'image du serveur est le helper attendu, avec ses droits.
    private Result AuthentifierLeServeur(NamedPipeClientStream client)
    {
        if (_cheminHelperAttendu is null)
            return Refus("pipe server not authenticated: no expected helper path was given to the client.");

        var image = PipeServerIdentity.TryGetServerImagePath(client);
        if (string.IsNullOrWhiteSpace(image))
            return Refus("pipe server not authenticated: the process serving the pipe cannot be found.");

        string attendu, trouve;
        try
        {
            attendu = Path.GetFullPath(_cheminHelperAttendu);
            trouve = Path.GetFullPath(image);
        }
        catch
        {
            return Refus("pipe server not authenticated: its image path is unreadable.");
        }

        if (!string.Equals(attendu, trouve, StringComparison.OrdinalIgnoreCase))
            return Refus($"pipe server not authenticated: expected process {attendu}, found {trouve}.");
        return PipeServerIdentity.RefusDePrivilege(client) is { } pourquoi
            ? Refus($"pipe server not authenticated: {pourquoi}.")
            : Result.Ok();
    }

    private static Result Refus(string pourquoi) => Result.Fail(ErrorCodes.ElevationDenied, pourquoi);

    // Se connecte en reprenant les échecs toutes les _connectRetryIntervalMs jusqu'à _connectTimeoutMs : true connecté,
    // false à l'échéance. L'annulation se propage.
    private async Task<bool> TryConnectWithRetryAsync(NamedPipeClientStream client, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, _connectTimeoutMs);
        while (true)
        {
            var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
            // Borner chaque tentative de connexion à l'intervalle de reprise (ou au budget restant)
            // pour qu'un seul ConnectAsync ne bloque pas toute l'échéance d'un coup.
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
                // Pipe pas encore prêt → reprendre jusqu'à l'échéance.
                if (Environment.TickCount64 >= deadline)
                    return false;
            }
        }
    }
}
