using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// §9.13 handshake STATE MACHINE — unit-tested behind seams (a real in-memory
// duplex stream pair, a fake caller authenticator, a deterministic key gen).
// NO real pipe, nothing elevated, no spawned process.
//
// The handshake establishes the EPHEMERAL session key that the existing typed
// HMAC layer then uses unchanged. Order under test (the spec's order):
//   AUTH the caller first  ->  then mint+send the key.
//
// Properties proven:
//   * caller-auth PASS  -> key negotiated, client can sign a typed op the server accepts
//   * caller-auth FAIL  -> fail-closed, NO key sent, no channel
//   * forged/garbage hello -> fail-clean, no key
//   * truncated handshake (peer closes early) -> fail-clean, no key, no throw
//   * the negotiated key actually drives the proven typed protocol (replay still rejected)
// -----------------------------------------------------------------------
public sealed class HandshakeStateMachineTests
{
    // A fixed key the fake generator hands out so the test can assert the exact bytes.
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

    // Builds a connected, full-duplex in-memory stream pair (a <-> b).
    private static (Stream a, Stream b) DuplexPair()
    {
        var a2b = new AnonymousPipeServerStreamPairFactory();
        return a2b.Create();
    }

    // ----------------------------------------------------------------
    // PASS path: auth ok -> the client receives the exact ephemeral key.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Auth_pass_negotiates_the_ephemeral_key_to_the_client()
    {
        var (clientSide, serverSide) = DuplexPair();

        // Server runs its half against serverSide; client runs against clientSide.
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
    // The negotiated key actually keys the PROVEN typed protocol: a request the
    // client signs with the negotiated key passes the server's HMAC gate.
    // (Proves we changed only HOW the key is obtained, not the message protocol.)
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

        // The negotiated key signs a typed request the existing TypedPrivilegedServer accepts.
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

        // Anti-replay still intact: replaying the same signed request is rejected.
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
    // FAIL-CLOSED: caller-auth denied -> server sends NO key, client gets none.
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
        // A well-formed peer that FAILED caller-auth → ELEVATION_DENIED (not a
        // transport/protocol error). This is the authorization-denial code.
        serverResult.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
        var clientResult = await clientTask;
        clientResult.IsOk.Should().BeFalse();
        clientResult.Value.Should().BeNull();
    }

    // ----------------------------------------------------------------
    // FIX 3: a malformed/garbage/unsupported handshake is a TRANSPORT/PROTOCOL
    // fault, not an authorization denial → HANDSHAKE_PROTOCOL_ERROR, fail-closed.
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

        // AUTH would PASS here — proving the code reflects the PROTOCOL fault, not auth.
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
        clientSide.Dispose(); // peer closes before sending the hello

        var serverResult = await new HandshakeServerCore(new AllowAuth(), new FixedKeyGen())
            .RunAsync(serverSide, new CallerInfo(4321), default);

        serverResult.IsOk.Should().BeFalse();
        serverResult.Error!.Value.Code.Should().Be(ErrorCodes.HandshakeProtocolError);
    }

    // ----------------------------------------------------------------
    // Forged / garbage hello (wrong protocol marker) -> fail-clean, no key.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Forged_hello_protocol_is_rejected_no_key()
    {
        var (clientSide, serverSide) = DuplexPair();

        // Client sends a hello with a BOGUS protocol marker, then tries to read a result.
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
        // Either the server replied Ok=false, or it closed — never a key.
        (clientResp is null || clientResp.Ok == false).Should().BeTrue();
        clientResp?.SessionKeyBase64.Should().BeNull();
    }

    // ----------------------------------------------------------------
    // Truncated handshake: the peer closes before sending the hello -> fail-clean.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Truncated_handshake_fails_clean_no_throw()
    {
        var (clientSide, serverSide) = DuplexPair();

        // Client closes immediately without sending a hello.
        clientSide.Dispose();

        var serverResult = await new HandshakeServerCore(new AllowAuth(), new FixedKeyGen())
            .RunAsync(serverSide, new CallerInfo(4321), default);

        serverResult.IsOk.Should().BeFalse();
    }

    // ----------------------------------------------------------------
    // Client sees a malformed/empty server reply -> fail-clean, no key.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Client_fails_clean_when_server_closes_without_a_result()
    {
        var (clientSide, serverSide) = DuplexPair();

        // Server reads the hello then closes without replying.
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
    // A key of the wrong size from a hostile server is rejected by the client.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Client_rejects_a_wrong_size_key()
    {
        var (clientSide, serverSide) = DuplexPair();

        var serverTask = Task.Run(async () =>
        {
            await WireCodec.ReadMessageAsync<HandshakeHello>(serverSide, default);
            var shortKey = Convert.ToBase64String(new byte[8]); // too short
            await WireCodec.WriteMessageAsync(
                serverSide, new HandshakeResult { Ok = true, SessionKeyBase64 = shortKey }, default);
        });

        var clientResult = await HandshakeClientCore.RunAsync(clientSide, default);
        await serverTask;

        clientResult.IsOk.Should().BeFalse();
        clientResult.Value.Should().BeNull();
    }
}

// Builds a connected full-duplex stream pair from two anonymous pipes so the two
// handshake halves can talk in-memory (no named pipe, nothing elevated).
internal sealed class AnonymousPipeServerStreamPairFactory
{
    public (Stream clientSide, Stream serverSide) Create()
    {
        // Two one-way anonymous pipes wired into a duplex via a bridging stream.
        var c2s = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.Out, System.IO.HandleInheritability.None);
        var c2sIn = new System.IO.Pipes.AnonymousPipeClientStream(
            System.IO.Pipes.PipeDirection.In, c2s.GetClientHandleAsString());
        var s2c = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.Out, System.IO.HandleInheritability.None);
        var s2cIn = new System.IO.Pipes.AnonymousPipeClientStream(
            System.IO.Pipes.PipeDirection.In, s2c.GetClientHandleAsString());

        var clientSide = new DuplexStream(s2cIn, c2s);   // client reads s2c, writes c2s
        var serverSide = new DuplexStream(c2sIn, s2c);   // server reads c2s, writes s2c
        return (clientSide, serverSide);
    }
}

// Minimal full-duplex stream that reads from one stream and writes to another.
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
