using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// TypedNamedPipeElevatedClient sérialise et signe une op typée
// via le codec et remonte les codes d'échec propre du serveur.
//
// Le client est piloté par une couture de transport en mémoire (ITypedRequestTransport)
// bouclée directement sur un vrai TypedPrivilegedServer. L'aller-retour
// client → codec → signature → serveur → liste blanche → exécution se joue de façon
// déterministe avec des faux : pas de vrai tube, rien d'élevé, rien ne touche au système.
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

    /// <summary>Boucle la requête du client directement sur un vrai serveur (même clé).</summary>
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
        // Altérer le JSON de l'op après signature → le HMAC ne correspond plus au corps.
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
