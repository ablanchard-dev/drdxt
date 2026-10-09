using System.Security.Cryptography;
using System.Text;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// L0.1 — le canal de retour de donnees.
//
// Avant lui, une lecture firmware n'apprenait que « la variable est lisible » : les octets
// etaient jetes dans PrivilegedOpExecutor. Le produit ne pouvait donc rien afficher de l'etat
// du firmware, et tout le lot BIOS restait bloque derriere ce trou.
//
// Ces tests font passer les octets par le vrai trajet : client -> codec -> signature ->
// serveur -> gardes -> allowlist -> executeur -> reponse -> client. Rien d'eleve, rien de
// natif, aucun tuyau reel -- la boucle en memoire du harnais existant.
//
// Ce qu'ils verrouillent, et qui casse si la logique casse :
//   * les octets arrivent INTACTS de bout en bout ;
//   * un executeur sans chemin de lecture ECHOUE au lieu de rendre un tableau vide ;
//   * un depassement de borne ECHOUE au lieu de tronquer ;
//   * le motif d'echec (« privilege inactif » = NON CONCLUANT) traverse VERBATIM ;
//   * une op d'ACTION ne porte aucune donnee, et son JSON reste celui d'avant ;
//   * l'allowlist garde le canal : un nom de variable non autorise n'atteint pas l'executeur.
// -----------------------------------------------------------------------
public sealed class DataReturnChannelTests
{
    private const string EfiGlobal = OpAllowlist.EfiGlobalVariableGuid;

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    private sealed class AllowAllAuth : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    private sealed class LoopbackTransport : ITypedRequestTransport
    {
        private readonly TypedPrivilegedServer _server;
        public LoopbackTransport(TypedPrivilegedServer server) => _server = server;
        public Task<PrivilegedResponse> SendAsync(TypedPrivilegedRequest request, CancellationToken ct)
            => _server.ProcessTypedRequestAsync(request, ct);
    }

    /// <summary>Executeur qui REPOND : rend les octets qu'on lui a confies.</summary>
    private sealed class ReadingExecutor : IPrivilegedOpExecutor
    {
        private readonly Result<byte[]> _answer;
        public ReadingExecutor(Result<byte[]> answer) => _answer = answer;
        public PrivilegedOp? Read { get; private set; }

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
            => Task.FromResult(Result.Ok());

        public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
        {
            Read = op;
            return Task.FromResult(_answer);
        }
    }

    /// <summary>Executeur qui n'implemente pas ReadAsync : il herite du refus par defaut.
    /// C'est la doublure de test typique, et le piege exact qu'on veut voir echouer bruyamment.</summary>
    private sealed class ActionOnlyExecutor : IPrivilegedOpExecutor
    {
        public bool ExecuteWasCalled { get; private set; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            ExecuteWasCalled = true;
            return Task.FromResult(Result.Ok());
        }
    }

    private static TypedNamedPipeElevatedClient ClientFor(byte[] key, IPrivilegedOpExecutor exec)
    {
        var server = new TypedPrivilegedServer(key, new AllowAllAuth(), new OpAllowlist(), exec);
        return new TypedNamedPipeElevatedClient(key, new LoopbackTransport(server));
    }

    private static ReadFirmwareVariableOp SecureBootRead() => new("SecureBoot", EfiGlobal);

    [Fact]
    public async Task A_read_carries_its_bytes_back_intact()
    {
        var key = NewKey();
        // 0x01 = Secure Boot actif. Un octet qui VEUT dire quelque chose : c'est precisement
        // ce que l'ancien canal jetait en ne rendant que « lisible ».
        var payload = new byte[] { 0x01, 0x00, 0xFF, 0x7E };
        var exec = new ReadingExecutor(Result<byte[]>.Ok(payload));

        var read = await ClientFor(key, exec).ReadAsync(SecureBootRead(), default);

        read.IsOk.Should().BeTrue();
        read.Value.Should().Equal(payload);
        exec.Read.Should().BeOfType<ReadFirmwareVariableOp>();
    }

    [Fact]
    public async Task An_executor_without_a_read_path_fails_instead_of_returning_nothing()
    {
        var key = NewKey();
        var exec = new ActionOnlyExecutor();

        var read = await ClientFor(key, exec).ReadAsync(SecureBootRead(), default);

        read.IsOk.Should().BeFalse();
        // Et surtout : pas de tableau vide, qui se lirait comme « le firmware a rendu 0 octet ».
        read.Value.Should().BeNull();
        // L'op de lecture ne doit pas non plus etre retombee sur le chemin d'ACTION, qui
        // aurait rendu un Ok vide indistinguable d'une lecture reussie.
        exec.ExecuteWasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task An_op_that_does_not_answer_fails_the_read_instead_of_returning_an_empty_array()
    {
        // Le test qui manquait. Il a ete ecrit apres qu'une mutation ait SURVECU : remplacer
        // l'echec par `Ok(Array.Empty<byte>())` ne faisait rougir personne, parce qu'aucun test
        // n'atteignait cette branche -- tous echouaient plus tot, cote serveur.
        //
        // Le cas reel : une op d'ACTION envoyee par ReadAsync. Le serveur la route vers le
        // chemin d'action, repond Ok sans charge utile, et c'est ici que se joue la difference
        // entre « cette op ne repond pas » et « le firmware a rendu zero octet ».
        var key = NewKey();
        var exec = new ReadingExecutor(Result<byte[]>.Ok(new byte[] { 0x01 }));

        var read = await ClientFor(key, exec).ReadAsync(new PingOp(), default);

        read.IsOk.Should().BeFalse();
        read.Value.Should().BeNull();
        read.Error!.Value.Message.Should().Contain("no data");
    }

    [Fact]
    public async Task A_payload_over_the_bound_fails_instead_of_being_truncated()
    {
        var key = NewKey();
        var tooBig = new byte[PrivilegedResponse.MaxDataBytes + 1];
        var exec = new ReadingExecutor(Result<byte[]>.Ok(tooBig));

        var read = await ClientFor(key, exec).ReadAsync(SecureBootRead(), default);

        read.IsOk.Should().BeFalse();
        read.Value.Should().BeNull();
        read.Error!.Value.Message.Should().Contain(PrivilegedResponse.MaxDataBytes.ToString());
    }

    [Fact]
    public async Task Exactly_the_bound_still_passes()
    {
        // La borne est un plafond INCLUSIF : sans ce test, la remplacer par >= passerait
        // inapercu et amputerait le canal d'un octet.
        var key = NewKey();
        var atBound = new byte[PrivilegedResponse.MaxDataBytes];
        atBound[^1] = 0x42;
        var exec = new ReadingExecutor(Result<byte[]>.Ok(atBound));

        var read = await ClientFor(key, exec).ReadAsync(SecureBootRead(), default);

        read.IsOk.Should().BeTrue();
        read.Value!.Length.Should().Be(PrivilegedResponse.MaxDataBytes);
        read.Value[^1].Should().Be(0x42);
    }

    [Fact]
    public async Task An_inconclusive_read_keeps_its_reason_verbatim()
    {
        // « SeSystemEnvironmentPrivilege inactif » n'est pas « le firmware a refuse ». Ecraser
        // ce motif en un echec generique transformerait un non concluant en verdict de fond --
        // l'erreur que ce projet a deja payee.
        var key = NewKey();
        const string reason =
            "Firmware variable 'SecureBoot' could not be read (win32=1314): "
            + "SeSystemEnvironmentPrivilege is not ACTIVE -- INCONCLUSIVE, not a firmware refusal.";
        var exec = new ReadingExecutor(Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, reason));

        var read = await ClientFor(key, exec).ReadAsync(SecureBootRead(), default);

        read.IsOk.Should().BeFalse();
        read.Error!.Value.Message.Should().Be(reason);
        read.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task The_allowlist_still_gates_the_read_channel()
    {
        // Le canal de retour n'est pas une porte derobee : une variable hors de l'ensemble
        // ferme est refusee avant l'executeur, exactement comme sur le chemin d'action.
        var key = NewKey();
        var exec = new ReadingExecutor(Result<byte[]>.Ok(new byte[] { 0x01 }));

        var read = await ClientFor(key, exec).ReadAsync(new ReadFirmwareVariableOp("Setup", EfiGlobal), default);

        read.IsOk.Should().BeFalse();
        read.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        exec.Read.Should().BeNull();
    }

    [Fact]
    public async Task An_action_response_carries_no_data_and_keeps_its_old_wire_form()
    {
        // Compatibilite : WireCodec refuse fail-closed tout membre inconnu. Si DataBase64
        // apparaissait dans la reponse d'une op d'ACTION, un client d'une version anterieure
        // cesserait de comprendre toutes les reponses, pas seulement les lectures.
        var key = NewKey();
        var exec = new ReadingExecutor(Result<byte[]>.Ok(new byte[] { 0x01 }));
        var server = new TypedPrivilegedServer(key, new AllowAllAuth(), new OpAllowlist(), exec);

        var opJson = PrivilegedOpCodec.Serialize(new PingOp()).Value!;
        var nonce = Guid.NewGuid().ToString("N");
        var request = new TypedPrivilegedRequest
        {
            OpJson = opJson,
            Nonce = nonce,
            HmacBase64 = TypedRequestHmac.Compute(key, opJson, nonce),
        };

        var response = await server.ProcessTypedRequestAsync(request, default);

        response.Ok.Should().BeTrue();
        response.DataBase64.Should().BeNull();

        // Et le JSON reellement emis par le codec du fil ne mentionne pas le champ.
        using var stream = new MemoryStream();
        await WireCodec.WriteMessageAsync(stream, response, default);
        var json = Encoding.UTF8.GetString(stream.ToArray(), 4, (int)stream.Length - 4);
        json.Should().NotContain("DataBase64");
    }

    [Fact]
    public async Task A_read_response_survives_the_real_wire_codec()
    {
        // La charge doit traverser le CADRAGE reel (prefixe de longueur + JSON), pas seulement
        // l'objet en memoire : c'est la que se paierait une borne mal calculee.
        var payload = RandomNumberGenerator.GetBytes(PrivilegedResponse.MaxDataBytes);
        var sent = new PrivilegedResponse { Ok = true, DataBase64 = Convert.ToBase64String(payload) };

        using var stream = new MemoryStream();
        await WireCodec.WriteMessageAsync(stream, sent, default);
        stream.Position = 0;
        var back = await WireCodec.ReadMessageAsync<PrivilegedResponse>(stream, default);

        back.Should().NotBeNull();
        Convert.FromBase64String(back!.DataBase64!).Should().Equal(payload);
    }
}
