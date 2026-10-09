using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// A3 — signed IPC wiring for TYPED PrivilegedOps, fail-clean.
//
// The server pipeline for a typed op is, IN ORDER:
//   (a) HMAC verify ............ else HELPER.SIGNATURE_INVALID (not executed)
//   (b) nonce not replayed ..... else HELPER.NOT_ALLOWLISTED   (not executed)
//   (c) caller authorized ...... else ELEVATION_DENIED         (not executed)
//   (d) codec deserialize ...... else HELPER.NOT_ALLOWLISTED   (not executed)
//   (e) OpAllowlist.Validate ... else HELPER.NOT_ALLOWLISTED   (not executed)
//   (f) execute the allowlisted op
//
// Deny-by-default, fail-clean — never fail-open. Any failure before (f) means
// the executor is never invoked.
// -----------------------------------------------------------------------
public sealed class TypedPrivilegedServerTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Records whether (and what) the server actually executed.</summary>
    private sealed class RecordingExecutor : IPrivilegedOpExecutor
    {
        public PrivilegedOp? Executed { get; private set; }
        public bool WasInvoked => Executed is not null;
        private readonly Result _result;
        public RecordingExecutor(Result? result = null) => _result = result ?? Result.Ok();

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Executed = op;
            return Task.FromResult(_result);
        }
    }

    private sealed class AllowAllAuth : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    private static TypedPrivilegedServer Server(
        byte[] key, IPrivilegedOpExecutor exec, ICallerAuthenticator? auth = null) =>
        new(key, auth ?? new AllowAllAuth(), new OpAllowlist(), exec);

    private static TypedPrivilegedRequest SignedRequest(byte[] key, PrivilegedOp op)
    {
        var json = PrivilegedOpCodec.Serialize(op).Value!;
        var nonce = Guid.NewGuid().ToString("N");
        var hmac = TypedRequestHmac.Compute(key, json, nonce);
        return new TypedPrivilegedRequest { OpJson = json, Nonce = nonce, HmacBase64 = hmac };
    }

    // --- (acceptance) allowlisted + signed op round-trips OK and IS executed ---

    [Fact]
    public async Task Allowlisted_signed_op_succeeds_and_is_executed()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(key, exec);

        var op = new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance);
        var response = await server.ProcessTypedRequestAsync(SignedRequest(key, op), default);

        response.Ok.Should().BeTrue();
        exec.WasInvoked.Should().BeTrue("an allowlisted, signed op must be executed");
        exec.Executed.Should().BeOfType<SetActivePowerPlanOp>();
    }

    // --- tampered HMAC → HELPER.SIGNATURE_INVALID, not executed ---

    [Fact]
    public async Task Tampered_signature_is_rejected_signature_invalid_not_executed()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(key, exec);

        var req = SignedRequest(key, new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance));
        // Flip one byte of the MAC.
        var bytes = Convert.FromBase64String(req.HmacBase64);
        bytes[0] ^= 0xFF;
        var tampered = req with { HmacBase64 = Convert.ToBase64String(bytes) };

        var response = await server.ProcessTypedRequestAsync(tampered, default);

        response.Ok.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.HelperSignatureInvalid);
        exec.WasInvoked.Should().BeFalse("a tampered op must never execute");
    }

    [Fact]
    public async Task Wrong_key_signature_is_rejected_signature_invalid()
    {
        var serverKey = NewKey();
        var clientKey = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(serverKey, exec);

        // Sign with the wrong key.
        var req = SignedRequest(clientKey, new CreateRestorePointOp("x"));
        var response = await server.ProcessTypedRequestAsync(req, default);

        response.ErrorCode.Should().Be(ErrorCodes.HelperSignatureInvalid);
        exec.WasInvoked.Should().BeFalse();
    }

    // --- non-allowlisted op (validly signed) → HELPER.NOT_ALLOWLISTED, not executed ---

    [Fact]
    public async Task NonAllowlisted_signed_op_is_rejected_before_execution()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(key, exec);

        // A perfectly-signed op that targets Defender — must be refused at the
        // allowlist gate, after signature passes, before execution.
        var evil = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend",
            "Start", RegistryValueType.DWord, "4");
        var response = await server.ProcessTypedRequestAsync(SignedRequest(key, evil), default);

        response.Ok.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.NotAllowlisted);
        exec.WasInvoked.Should().BeFalse("a non-allowlisted op must never execute");
    }

    // --- unknown discriminant in the signed payload → HELPER.NOT_ALLOWLISTED ---

    [Fact]
    public async Task Unknown_op_payload_is_rejected_not_allowlisted_not_executed()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(key, exec);

        // Sign an arbitrary JSON blob (no valid discriminant). Signature passes;
        // the codec refuses to bind it → NOT_ALLOWLISTED.
        var json = """{"op":99999,"cmd":"calc"}""";
        var nonce = Guid.NewGuid().ToString("N");
        var hmac = TypedRequestHmac.Compute(key, json, nonce);
        var req = new TypedPrivilegedRequest { OpJson = json, Nonce = nonce, HmacBase64 = hmac };

        var response = await server.ProcessTypedRequestAsync(req, default);

        response.ErrorCode.Should().Be(ErrorCodes.NotAllowlisted);
        exec.WasInvoked.Should().BeFalse();
    }

    // --- caller not authorized → ELEVATION_DENIED, not executed (fail-closed) ---

    [Fact]
    public async Task Default_authenticator_denies_before_execution()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = new TypedPrivilegedServer(
            key, new DenyByDefaultCallerAuthenticator(), new OpAllowlist(), exec);

        var req = SignedRequest(key, new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance));
        var response = await server.ProcessTypedRequestAsync(req, default);

        response.Ok.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.ElevationDenied);
        exec.WasInvoked.Should().BeFalse();
    }

    // --- replayed nonce → rejected, not executed twice ---

    [Fact]
    public async Task Replayed_nonce_second_is_rejected_not_executed_again()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(key, exec);
        var req = SignedRequest(key, new CreateRestorePointOp(CreateRestorePointOp.NomDrDxt));

        var first = await server.ProcessTypedRequestAsync(req, default);
        var second = await server.ProcessTypedRequestAsync(req, default);

        first.Ok.Should().BeTrue();
        second.Ok.Should().BeFalse("replaying a nonce must be refused");
    }

    // --- an execution failure surfaces as HELPER.OP_FAILED (not a throw) ---

    [Fact]
    public async Task Executor_failure_surfaces_as_op_failed()
    {
        var key = NewKey();
        var exec = new RecordingExecutor(Result.Fail(ErrorCodes.HelperOpFailed, "sdk error"));
        var server = Server(key, exec);

        var req = SignedRequest(key, new CreateRestorePointOp(CreateRestorePointOp.NomDrDxt));
        var response = await server.ProcessTypedRequestAsync(req, default);

        response.Ok.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Executor_that_throws_is_wrapped_as_op_failed_no_throw()
    {
        var key = NewKey();
        var server = new TypedPrivilegedServer(key, new AllowAllAuth(), new OpAllowlist(), new ThrowingExecutor());

        var req = SignedRequest(key, new CreateRestorePointOp(CreateRestorePointOp.NomDrDxt));
        Func<Task> act = async () => await server.ProcessTypedRequestAsync(req, default);

        await act.Should().NotThrowAsync("a throwing executor must be wrapped, never propagate across IPC");
        var response = await server.ProcessTypedRequestAsync(SignedRequest(key, new CreateRestorePointOp(CreateRestorePointOp.NomDrDxt)), default);
        response.ErrorCode.Should().Be(ErrorCodes.HelperOpFailed);
    }

    private sealed class ThrowingExecutor : IPrivilegedOpExecutor
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    // --- malformed / null request → fail-clean, never throws, never executes ---

    [Fact]
    public async Task Null_request_fails_clean_no_throw()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(key, exec);

        Func<Task> act = async () => await server.ProcessTypedRequestAsync(null, default);
        await act.Should().NotThrowAsync();

        var response = await server.ProcessTypedRequestAsync(null, default);
        response.Ok.Should().BeFalse();
        exec.WasInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task Null_hmac_field_fails_clean_signature_invalid()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(key, exec);
        var json = PrivilegedOpCodec.Serialize(new CreateRestorePointOp("x")).Value!;
        var req = new TypedPrivilegedRequest { OpJson = json, Nonce = "n", HmacBase64 = null! };

        Func<Task> act = async () => await server.ProcessTypedRequestAsync(req, default);
        await act.Should().NotThrowAsync();

        var response = await server.ProcessTypedRequestAsync(req, default);
        response.ErrorCode.Should().Be(ErrorCodes.HelperSignatureInvalid);
        exec.WasInvoked.Should().BeFalse();
    }
}

// -----------------------------------------------------------------------
// GUARD: the server has NO generic registry/exec path. The only way to make it
// execute is to go through a closed PrivilegedOp leaf that passes the allowlist.
// This pins the master-plan §9.13 invariant against future drift.
// -----------------------------------------------------------------------
public sealed class TypedServerHasNoGenericPathTests
{
    [Fact]
    public void Executor_contract_only_accepts_typed_PrivilegedOp()
    {
        // The executor seam takes a PrivilegedOp — the SEALED, closed hierarchy.
        // There is no string/command/path overload anywhere on the contract.
        var method = typeof(IPrivilegedOpExecutor).GetMethod(nameof(IPrivilegedOpExecutor.ExecuteAsync))!;
        var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
        paramTypes.Should().Contain(typeof(PrivilegedOp));
        paramTypes.Should().NotContain(typeof(string), "no free string command/path may reach the executor");
    }

    [Fact]
    public void TypedServer_exposes_no_method_taking_a_raw_string_command()
    {
        // No public method on the server accepts a bare string (a command/path)
        // as its sole privileged input — the only entry point is the typed,
        // signed, allowlisted request.
        var stringEntryPoints = typeof(TypedPrivilegedServer)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(TypedPrivilegedServer))
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .ToArray();

        stringEntryPoints.Should().BeEmpty(
            "the only privileged entry point is a typed, signed, allowlisted request — never a raw string");
    }
}
