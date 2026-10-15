using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Machine à états de la négociation, testée derrière des coutures (une vraie paire de flux
// duplex en mémoire, un faux authentificateur d'appelant, une génération de clé déterministe).
// Pas de vrai tube, rien d'élevé, aucun processus lancé.
//
// La négociation établit la clé de session éphémère que la couche HMAC typée existante
// utilise ensuite inchangée. Ordre testé (celui de la spécification) :
//   authentifier l'appelant d'abord → puis forger et envoyer la clé.
//
// Propriétés prouvées :
//   * authentification réussie → clé négociée, le client signe une op typée que le serveur accepte
//   * authentification refusée → fermé, aucune clé envoyée, pas de canal
//   * hello forgé ou corrompu → échec propre, pas de clé
//   * négociation tronquée (le pair ferme tôt) → échec propre, pas de clé, pas d'exception
//   * la clé négociée pilote vraiment le protocole typé prouvé (le rejeu reste refusé)
// -----------------------------------------------------------------------
public sealed class HandshakeStateMachineTests
{
    // Clé fixe que rend le faux générateur, pour vérifier les octets exacts.
    private static readonly byte[] FixedKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private sealed class FixedKeyGen : ISessionKeyGenerator
    {
        public byte[] NewSessionKey() => (byte[])FixedKey.Clone();
    }

    private sealed class AllowAuth : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    private sealed class DenyAuth : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) =>
            Result.Fail(ErrorCodes.ElevationDenied, "denied");
    }

    // Construit une paire de flux duplex connectés en mémoire (a <-> b).
    private static (Stream a, Stream b) DuplexPair()
    {
        var a2b = new AnonymousPipeServerStreamPairFactory();
        return a2b.Create();
    }

    // ----------------------------------------------------------------
    // Chemin nominal : authentification ok → le client reçoit la clé éphémère exacte.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Auth_pass_negotiates_the_ephemeral_key_to_the_client()
    {
        var (clientSide, serverSide) = DuplexPair();

        // Le serveur joue sa moitié sur serverSide, le client sur clientSide.
        var serverTask = new HandshakeServerCore(new AllowAuth(), new FixedKeyGen())
            .RunAsync(serverSide, new CallerInfo(4321), default);
        var clientTask = HandshakeClientCore.RunAsync(clientSide, default);

        await Task.WhenAll(serverTask, clientTask);

        var serverResult = await serverTask;
        var clientResult = await clientTask;

        serverResult.IsOk.Should().BeTrue();
        clientResult.IsOk.Should().BeTrue();
        clientResult.Value.Should().Equal(FixedKey);
    }

    // ----------------------------------------------------------------
    // La clé négociée alimente bien le protocole typé prouvé : une requête que le client signe
    // avec elle passe la porte HMAC du serveur.
    // (Seule la façon d'obtenir la clé change, pas le protocole des messages.)
    // ----------------------------------------------------------------
    [Fact]
    public async Task Negotiated_key_drives_the_proven_typed_protocol()
    {
        var (clientSide, serverSide) = DuplexPair();
        var serverTask = new HandshakeServerCore(new AllowAuth(), new FixedKeyGen())
            .RunAsync(serverSide, new CallerInfo(4321), default);
        var clientResult = await HandshakeClientCore.RunAsync(clientSide, default);
        await serverTask;

        var negotiated = clientResult.Value!;

        // La clé négociée signe une requête typée que le TypedPrivilegedServer existant accepte.
        var op = new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance);
        var json = PrivilegedOpCodec.Serialize(op).Value!;
        var nonce = Guid.NewGuid().ToString("N");
        var hmac = TypedRequestHmac.Compute(negotiated, json, nonce);
        var request = new TypedPrivilegedRequest { OpJson = json, Nonce = nonce, HmacBase64 = hmac };

        var exec = new RecordingExec();
        var typedServer = new TypedPrivilegedServer(
            negotiated, new AllowAuth(), new OpAllowlist(), exec);

        var resp = await typedServer.ProcessTypedRequestAsync(request, default, new CallerInfo(4321));
        resp.Ok.Should().BeTrue();
        exec.WasInvoked.Should().BeTrue();

        // L'anti-rejeu tient toujours : rejouer la même requête signée est refusé.
        var replay = await typedServer.ProcessTypedRequestAsync(request, default, new CallerInfo(4321));
        replay.Ok.Should().BeFalse();
    }

    private sealed class RecordingExec : IPrivilegedOpExecutor
    {
        public bool WasInvoked { get; private set; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            WasInvoked = true;
            return Task.FromResult(Result.Ok());
        }
    }

    // ----------------------------------------------------------------
    // Fermé : authentification refusée → le serveur n'envoie aucune clé, le client n'en reçoit aucune.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Auth_fail_is_fail_closed_no_key_negotiated()
    {
        var (clientSide, serverSide) = DuplexPair();

        var serverTask = new HandshakeServerCore(new DenyAuth(), new FixedKeyGen())
            .RunAsync(serverSide, new CallerInfo(4321), default);
        var clientTask = HandshakeClientCore.RunAsync(clientSide, default);

        await Task.WhenAll(serverTask, clientTask);

        var serverResult = await serverTask;
        serverResult.IsOk.Should().BeFalse();
        // Un pair bien formé dont l'authentification a échoué → ELEVATION_DENIED (pas une erreur
        // de transport ou de protocole) : c'est le code du refus d'autorisation.
        serverResult.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
        var clientResult = await clientTask;
        clientResult.IsOk.Should().BeFalse();
        clientResult.Value.Should().BeNull();
    }

    // ----------------------------------------------------------------
    // Une négociation malformée, corrompue ou d'une version inconnue est une faute de transport
    // ou de protocole, pas un refus d'autorisation → HANDSHAKE_PROTOCOL_ERROR, fermé.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Forged_protocol_returns_handshake_protocol_error_not_auth_denied()
    {
        var (clientSide, serverSide) = DuplexPair();

        var clientTask = Task.Run(async () =>
        {
            await WireCodec.WriteMessageAsync(
                clientSide, new HandshakeHello { Protocol = "evil-v9" }, default);
            return await WireCodec.ReadMessageAsync<HandshakeResult>(clientSide, default);
        });

        // L'authentification passerait ici : le code reflète bien la faute de protocole, pas l'authentification.
        var serverResult = await new HandshakeServerCore(new AllowAuth(), new FixedKeyGen())
            .RunAsync(serverSide, new CallerInfo(4321), default);

        serverResult.IsOk.Should().BeFalse();
        serverResult.Error!.Value.Code.Should().Be(ErrorCodes.HandshakeProtocolError);
        serverResult.Error!.Value.Code.Should().NotBe(ErrorCodes.ElevationDenied);

        var clientResp = await clientTask;
        (clientResp is null || clientResp.Ok == false).Should().BeTrue();
        clientResp?.SessionKeyBase64.Should().BeNull();
    }

    [Fact]
    public async Task Truncated_handshake_returns_handshake_protocol_error()
    {
        var (clientSide, serverSide) = DuplexPair();
        clientSide.Dispose(); // le pair ferme avant d'envoyer le hello

        var serverResult = await new HandshakeServerCore(new AllowAuth(), new FixedKeyGen())
            .RunAsync(serverSide, new CallerInfo(4321), default);

        serverResult.IsOk.Should().BeFalse();
        serverResult.Error!.Value.Code.Should().Be(ErrorCodes.HandshakeProtocolError);
    }

    // ----------------------------------------------------------------
    // Hello forgé ou corrompu (mauvais marqueur de protocole) → échec propre, pas de clé.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Forged_hello_protocol_is_rejected_no_key()
    {
        var (clientSide, serverSide) = DuplexPair();

        // Le client envoie un hello avec un marqueur de protocole bidon, puis tente de lire un résultat.
        var clientTask = Task.Run(async () =>
        {
            await WireCodec.WriteMessageAsync(
                clientSide, new HandshakeHello { Protocol = "evil-v9" }, default);
            return await WireCodec.ReadMessageAsync<HandshakeResult>(clientSide, default);
        });

        var serverResult = await new HandshakeServerCore(new AllowAuth(), new FixedKeyGen())
            .RunAsync(serverSide, new CallerInfo(4321), default);

        serverResult.IsOk.Should().BeFalse();
        var clientResp = await clientTask;
        // Soit le serveur a répondu Ok=false, soit il a fermé : jamais une clé.
        (clientResp is null || clientResp.Ok == false).Should().BeTrue();
        clientResp?.SessionKeyBase64.Should().BeNull();
    }

    // ----------------------------------------------------------------
    // Négociation tronquée : le pair ferme avant d'envoyer le hello → échec propre.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Truncated_handshake_fails_clean_no_throw()
    {
        var (clientSide, serverSide) = DuplexPair();

        // Le client ferme aussitôt sans envoyer de hello.
        clientSide.Dispose();

        var serverResult = await new HandshakeServerCore(new AllowAuth(), new FixedKeyGen())
            .RunAsync(serverSide, new CallerInfo(4321), default);

        serverResult.IsOk.Should().BeFalse();
    }

    // ----------------------------------------------------------------
    // Le client voit une réponse serveur malformée ou vide → échec propre, pas de clé.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Client_fails_clean_when_server_closes_without_a_result()
    {
        var (clientSide, serverSide) = DuplexPair();

        // Le serveur lit le hello puis ferme sans répondre.
        var serverTask = Task.Run(async () =>
        {
            await WireCodec.ReadMessageAsync<HandshakeHello>(serverSide, default);
            serverSide.Dispose();
        });

        var clientResult = await HandshakeClientCore.RunAsync(clientSide, default);
        await serverTask;

        clientResult.IsOk.Should().BeFalse();
        clientResult.Value.Should().BeNull();
    }

    // ----------------------------------------------------------------
    // Une clé de mauvaise taille venue d'un serveur hostile est refusée par le client.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Client_rejects_a_wrong_size_key()
    {
        var (clientSide, serverSide) = DuplexPair();

        var serverTask = Task.Run(async () =>
        {
            await WireCodec.ReadMessageAsync<HandshakeHello>(serverSide, default);
            var shortKey = Convert.ToBase64String(new byte[8]); // trop courte
            await WireCodec.WriteMessageAsync(
                serverSide, new HandshakeResult { Ok = true, SessionKeyBase64 = shortKey }, default);
        });

        var clientResult = await HandshakeClientCore.RunAsync(clientSide, default);
        await serverTask;

        clientResult.IsOk.Should().BeFalse();
        clientResult.Value.Should().BeNull();
    }
}

// Construit une paire de flux duplex connectés à partir de deux tubes anonymes, pour que les
// deux moitiés de la négociation se parlent en mémoire (pas de tube nommé, rien d'élevé).
internal sealed class AnonymousPipeServerStreamPairFactory
{
    public (Stream clientSide, Stream serverSide) Create()
    {
        // Deux tubes anonymes à sens unique câblés en duplex par un flux pont.
        var c2s = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.Out, System.IO.HandleInheritability.None);
        var c2sIn = new System.IO.Pipes.AnonymousPipeClientStream(
            System.IO.Pipes.PipeDirection.In, c2s.GetClientHandleAsString());
        var s2c = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.Out, System.IO.HandleInheritability.None);
        var s2cIn = new System.IO.Pipes.AnonymousPipeClientStream(
            System.IO.Pipes.PipeDirection.In, s2c.GetClientHandleAsString());

        var clientSide = new DuplexStream(s2cIn, c2s);   // le client lit s2c, écrit c2s
        var serverSide = new DuplexStream(c2sIn, s2c);   // le serveur lit c2s, écrit s2c
        return (clientSide, serverSide);
    }
}

// Flux duplex minimal qui lit sur un flux et écrit sur un autre.
internal sealed class DuplexStream : Stream
{
    private readonly Stream _in;
    private readonly Stream _out;
    public DuplexStream(Stream input, Stream output) { _in = input; _out = output; }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _out.Flush();
    public override Task FlushAsync(CancellationToken ct) => _out.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _in.ReadAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _out.WriteAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _in.Dispose(); _out.Dispose(); }
        base.Dispose(disposing);
    }
}
