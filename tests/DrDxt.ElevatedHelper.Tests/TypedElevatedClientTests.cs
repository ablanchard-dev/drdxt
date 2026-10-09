using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// A3 (client side) — TypedNamedPipeElevatedClient serializes+signs a typed op
// via the codec and surfaces the server's fail-clean codes.
//
// We drive the client over an in-memory transport seam (ITypedRequestTransport)
// looped straight into a real TypedPrivilegedServer. This exercises the
// client→codec→sign→server→allowlist→execute round-trip deterministically with
// fakes — no real pipe, nothing elevated, nothing touches the system.
// -----------------------------------------------------------------------
public sealed class TypedElevatedClientTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    private sealed class RecordingExecutor : IPrivilegedOpExecutor
    {
        public PrivilegedOp? Executed { get; private set; }
        public bool WasInvoked => Executed is not null;
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Executed = op;
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class AllowAllAuth : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    /// <summary>Loops the client's request straight into a real server (same key).</summary>
    private sealed class LoopbackTransport : ITypedRequestTransport
    {
        private readonly TypedPrivilegedServer _server;
        private readonly Func<TypedPrivilegedRequest, TypedPrivilegedRequest>? _tamper;
        public LoopbackTransport(TypedPrivilegedServer server,
            Func<TypedPrivilegedRequest, TypedPrivilegedRequest>? tamper = null)
        {
            _server = server;
            _tamper = tamper;
        }

        public Task<PrivilegedResponse> SendAsync(TypedPrivilegedRequest request, CancellationToken ct)
        {
            var toSend = _tamper is null ? request : _tamper(request);
            return _server.ProcessTypedRequestAsync(toSend, ct);
        }
    }

    private static (TypedNamedPipeElevatedClient client, RecordingExecutor exec) BuildPair(
        byte[] key, Func<TypedPrivilegedRequest, TypedPrivilegedRequest>? tamper = null)
    {
        var exec = new RecordingExecutor();
        var server = new TypedPrivilegedServer(key, new AllowAllAuth(), new OpAllowlist(), exec);
        var client = new TypedNamedPipeElevatedClient(key, new LoopbackTransport(server, tamper));
        return (client, exec);
    }

    [Fact]
    public async Task Allowlisted_signed_op_roundtrips_ok()
    {
        var key = NewKey();
        var (client, exec) = BuildPair(key);

        var result = await client.ExecuteAsync(new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance), default);

        result.IsOk.Should().BeTrue();
        exec.WasInvoked.Should().BeTrue();
        exec.Executed.Should().BeOfType<SetActivePowerPlanOp>();
    }

    [Fact]
    public async Task Tampered_signature_yields_signature_invalid_not_executed()
    {
        var key = NewKey();
        var (client, exec) = BuildPair(key, tamper: req =>
        {
            var bytes = Convert.FromBase64String(req.HmacBase64);
            bytes[0] ^= 0xFF;
            return req with { HmacBase64 = Convert.ToBase64String(bytes) };
        });

        var result = await client.ExecuteAsync(new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance), default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperSignatureInvalid);
        exec.WasInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task Unsigned_op_payload_tampered_yields_signature_invalid()
    {
        // Tamper the op JSON after signing → the HMAC no longer matches the body.
        var key = NewKey();
        var (client, exec) = BuildPair(key, tamper: req =>
            req with { OpJson = req.OpJson.Replace("batch", "tampered-description") });

        var result = await client.ExecuteAsync(new CreateRestorePointOp("batch"), default);

        result.Error!.Value.Code.Should().Be(ErrorCodes.HelperSignatureInvalid);
        exec.WasInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task NonAllowlisted_op_yields_not_allowlisted_not_executed()
    {
        var key = NewKey();
        var (client, exec) = BuildPair(key);

        var evil = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend",
            "Start", RegistryValueType.DWord, "4");
        var result = await client.ExecuteAsync(evil, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        exec.WasInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task Client_refuses_null_op_clientside_no_send()
    {
        var key = NewKey();
        var (client, exec) = BuildPair(key);

        var result = await client.ExecuteAsync(null!, default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        exec.WasInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task Client_implements_IElevatedClient()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = new TypedPrivilegedServer(key, new AllowAllAuth(), new OpAllowlist(), exec);
        IElevatedClient client = new TypedNamedPipeElevatedClient(key, new LoopbackTransport(server));

        var result = await client.ExecuteAsync(new PingOp(), default);

        result.IsOk.Should().BeTrue();
    }
}
