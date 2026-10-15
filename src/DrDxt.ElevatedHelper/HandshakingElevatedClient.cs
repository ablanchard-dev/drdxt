using System.IO.Pipes;
using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// HandshakingElevatedClient : l'IElevatedClient côté App qui négocie la clé de session sur
// le pipe au lieu de lire une clé partagée d'avance dans une variable d'environnement.
//
// Par op (une connexion) : connexion → handshake (obtenir la clé éphémère que le serveur
// vérifié a frappée pour nous) → sérialiser l'op par le codec borné → HMAC du couple canonique
// (opJson, nonce) avec la clé négociée → envoyer la requête typée → lire la réponse. Codec,
// HMAC et tramage sont les pièces éprouvées, intactes ; seule la provenance de la clé change.
//
// L'anti-rejeu est gardé : un nonce neuf par requête, et la clé éphémère est propre à la
// connexion. Toute panne de transport est emballée → un Result en échec propre ; aucune
// exception ne s'échappe (l'App se rabat sur UnavailableElevatedClient).
//
// Authentification du serveur (defaut D.3). Elle est symetrique de celle du serveur : celui-ci
// resout le pid de l'appelant depuis le tube (GetNamedPipeClientProcessId → chemin + SHA-256,
// HandshakeServerCore etape 3) ; le client resout le pid du serveur (GetNamedPipeServerProcessId)
// et compare son image au chemin attendu du helper. Sans elle, le nom du tube etant une chaine
// fixe et publique, un processus non privilegie du meme compte pourrait le prendre avant le
// helper (qui, lui, le detecte : FirstPipeInstance echoue) puis repondre Ok a tout : l'App
// croirait une op privilegiee appliquee alors que rien ne l'est, et le squatteur recevrait la
// liste des ops demandees. Ce n'est pas une elevation, c'est un mensonge rendu au client.
//
// La garde est fail-closed et nommee : sans chemin attendu, sans pid resoluble, sans image
// lisible, ou sur un chemin qui differe, la session est refusee avant le handshake avec un
// message qui dit lequel des quatre cas s'est produit. Le refus et le cas legitime sont
// tous deux mesures en in-process (TubeUsurpeTests / HandshakeChannelIntegrationTests) :
// une garde dont seul le refus serait prouve pourrait refuser tout, toujours, sous une
// suite verte.
//
// La traversee d'integrite reelle (App moyenne → helper eleve) se mesure avec
// `tools/helperhandshake`, qui lance a
// integrite moyenne (0x2000, lu par GetTokenInformation sur le processus vivant), helper eleve
// par UAC, garde du serveur active → OVERALL PASS, cle negociee, deux Ping. Les tests, eux,
// tournent toujours a integrite egale : cette preuve-la est un banc manuel, pas un test. Le choix
// de PROCESS_QUERY_LIMITED_INFORMATION plutot que de Process.MainModule est fait pour ce cas
// precis (voir PipeServerIdentity).
//
// Limite assumee : la comparaison porte sur le chemin de l'image, pas sur son empreinte ni
// sur son niveau d'integrite. Un attaquant qui lancerait le vrai helper non eleve depuis le
// vrai chemin passerait la garde, mais il echouerait alors sur les ops privilegiees, donc
// il rendrait un echec honnete, pas un faux « applique ». Et l'injection intra-utilisateur
// bat de toute facon toute verification d'image (risque connu et accepte, D.4).
//
// La latence de démarrage s'absorbe ici, dans la connexion de la vraie op : une boucle de
// reprise de connexion bornée (échecs repris toutes les ~pollIntervalMs jusqu'à
// connectTimeoutMs), pour que la toute première op après un lancement à la demande atteigne le
// helper juste démarré sans qu'une sonde de vie séparée consomme une acceptation. Le handshake
// lui-même n'est tenté qu'une fois par connexion réussie (pas de reprise : un rejet est
// définitif), il y a donc exactement une acceptation pour l'op.
//
// Note : l'abstraction client vit ici pour l'instant ; elle pourrait passer dans
// DrDxt.Infrastructure (« élévation client ») sans changer le canal.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class HandshakingElevatedClient : IElevatedClient
{
    private readonly string _pipeName;
    private readonly string? _cheminHelperAttendu;
    private readonly int _connectTimeoutMs;
    private readonly int _connectRetryIntervalMs;

    /// <param name="expectedServerImagePath">Le chemin complet de l'image du helper que ce
    /// client accepte d'ecouter. Obligatoire de fait : un null (ou un chemin vide) ne
    /// desactive pas la garde, il la fait refuser : aucun appelant ne peut donc obtenir un
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

    /// <summary>Le canal de retour de donnees sur le chemin de production (celui que l'app
    /// utilise reellement). Sans cette surcharge, une lecture partie de l'app tomberait
    /// sur le refus par defaut de IElevatedClient : un echec honnete, mais qui ferait
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
        // Sérialiser d'abord par le codec borné : refuser une feuille inconnue côté client
        // avant que quoi que ce soit parte sur le fil (défense en profondeur ; le serveur revérifie).
        var serialized = PrivilegedOpCodec.Serialize(op);
        if (!serialized.IsOk || serialized.Value is null)
            return Result<PrivilegedResponse>.Fail(ErrorCodes.NotAllowlisted, "Unsupported privileged operation.");
        var opJson = serialized.Value;

        byte[]? sessionKey = null;
        try
        {
            using var client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            // Reprise de connexion bornée : un helper juste lancé peut ne pas servir son pipe
            // FirstPipeInstance pendant quelques centaines de ms. Reprendre les échecs de connexion
            // jusqu'à l'échéance globale, puis échouer proprement en HELPER_UNREACHABLE.
            if (!await TryConnectWithRetryAsync(client, ct).ConfigureAwait(false))
                return Result<PrivilegedResponse>.Fail(ErrorCodes.HelperUnreachable,
                    "Could not reach the elevated helper (it did not start serving in time).");

            // Authentification du serveur, avant le handshake : on ne dit rien a un tube dont
            // on ne sait pas qui le sert. Poser la garde ici et pas apres n'est pas cosmetique :
            // le handshake livre au pair la liste des ops que DrDXT veut appliquer.
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
                Array.Clear(sessionKey); // la clé éphémère meurt avec la connexion
        }
    }

    // La garde. Quatre refus nommes, aucun silence, aucun repli : « pas de chemin attendu »,
    // « processus introuvable », « image illisible » et « mauvaise image » se distinguent dans
    // le message, parce qu'ils ne se corrigent pas pareil. Un seul chemin rend Ok : celui ou
    // l'image du serveur est le helper attendu.
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

        return string.Equals(attendu, trouve, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok()
            : Refus($"pipe server not authenticated: expected process {attendu}, found {trouve}.");
    }

    private static Result Refus(string pourquoi) => Result.Fail(ErrorCodes.ElevationDenied, pourquoi);

    // Se connecte au pipe du helper en reprenant les échecs de connexion (le helper peut encore
    // démarrer après un lancement à la demande) toutes les _connectRetryIntervalMs jusqu'à
    // l'échéance globale _connectTimeoutMs. Rend true sur un flux connecté ; false si l'échéance
    // est passée sans connexion. L'annulation se propage.
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
