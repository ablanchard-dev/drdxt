using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Câblage IPC signé des PrivilegedOp typées, échec propre.
//
// Le pipeline du serveur pour une op typée, dans l'ordre :
//   (a) vérification HMAC ............ sinon HELPER.SIGNATURE_INVALID (non exécutée)
//   (b) nonce non rejoué ............. sinon HELPER.NOT_ALLOWLISTED   (non exécutée)
//   (c) appelant autorisé ............ sinon ELEVATION_DENIED         (non exécutée)
//   (d) désérialisation codec ........ sinon HELPER.NOT_ALLOWLISTED   (non exécutée)
//   (e) OpAllowlist.Validate ......... sinon HELPER.NOT_ALLOWLISTED   (non exécutée)
//   (f) exécution de l'op autorisée
//
// Refus par défaut, échec propre, jamais ouvert. Tout échec avant (f) signifie que
// l'exécuteur n'est jamais appelé.
// -----------------------------------------------------------------------
public sealed class TypedPrivilegedServerTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Enregistre si (et ce que) le serveur a réellement exécuté.</summary>
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

    // --- (acceptation) une op autorisée et signée fait l'aller-retour Ok et est exécutée ---

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

    // --- HMAC altéré → HELPER.SIGNATURE_INVALID, non exécutée ---

    [Fact]
    public async Task Tampered_signature_is_rejected_signature_invalid_not_executed()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(key, exec);

        var req = SignedRequest(key, new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance));
        // Inverser un octet du MAC.
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

        // Signer avec la mauvaise clé.
        var req = SignedRequest(clientKey, new CreateRestorePointOp("x"));
        var response = await server.ProcessTypedRequestAsync(req, default);

        response.ErrorCode.Should().Be(ErrorCodes.HelperSignatureInvalid);
        exec.WasInvoked.Should().BeFalse();
    }

    // --- op hors liste blanche (bien signée) → HELPER.NOT_ALLOWLISTED, non exécutée ---

    [Fact]
    public async Task NonAllowlisted_signed_op_is_rejected_before_execution()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(key, exec);

        // Une op parfaitement signée qui vise Defender : refusée à la porte de la liste blanche,
        // après la signature, avant l'exécution.
        var evil = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend",
            "Start", RegistryValueType.DWord, "4");
        var response = await server.ProcessTypedRequestAsync(SignedRequest(key, evil), default);

        response.Ok.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.NotAllowlisted);
        exec.WasInvoked.Should().BeFalse("a non-allowlisted op must never execute");
    }

    // --- discriminant inconnu dans la charge signée → HELPER.NOT_ALLOWLISTED ---

    [Fact]
    public async Task Unknown_op_payload_is_rejected_not_allowlisted_not_executed()
    {
        var key = NewKey();
        var exec = new RecordingExecutor();
        var server = Server(key, exec);

        // Signer un JSON arbitraire (sans discriminant valide). La signature passe ;
        // le codec refuse de le lier → NOT_ALLOWLISTED.
        var json = """{"op":99999,"cmd":"calc"}""";
        var nonce = Guid.NewGuid().ToString("N");
        var hmac = TypedRequestHmac.Compute(key, json, nonce);
        var req = new TypedPrivilegedRequest { OpJson = json, Nonce = nonce, HmacBase64 = hmac };

        var response = await server.ProcessTypedRequestAsync(req, default);

        response.ErrorCode.Should().Be(ErrorCodes.NotAllowlisted);
        exec.WasInvoked.Should().BeFalse();
    }

    // --- appelant non autorisé → ELEVATION_DENIED, non exécutée (fermé) ---

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

    // --- nonce rejoué → refusé, pas exécuté deux fois ---

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

    // --- un échec d'exécution remonte en HELPER.OP_FAILED (pas une exception) ---

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

    // --- requête malformée ou nulle → échec propre, jamais d'exception, jamais exécutée ---

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
// Garde : le serveur n'a aucun chemin générique registre/exécution. La seule façon de le
// faire exécuter est de passer par une feuille PrivilegedOp fermée qui franchit la liste blanche.
// Ceci fige l'invariant de feuille exacte contre toute dérive.
// -----------------------------------------------------------------------
public sealed class TypedServerHasNoGenericPathTests
{
    [Fact]
    public void Executor_contract_only_accepts_typed_PrivilegedOp()
    {
        // La couture d'exécuteur prend une PrivilegedOp : la hiérarchie scellée, fermée.
        // Aucune surcharge chaîne/commande/chemin nulle part sur le contrat.
        var method = typeof(IPrivilegedOpExecutor).GetMethod(nameof(IPrivilegedOpExecutor.ExecuteAsync))!;
        var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
        paramTypes.Should().Contain(typeof(PrivilegedOp));
        paramTypes.Should().NotContain(typeof(string), "no free string command/path may reach the executor");
    }

    [Fact]
    public void TypedServer_exposes_no_method_taking_a_raw_string_command()
    {
        // Aucune méthode publique du serveur n'accepte une simple chaîne (commande ou chemin)
        // comme seule entrée privilégiée : le seul point d'entrée est la requête typée,
        // signée, passée par la liste blanche.
        var stringEntryPoints = typeof(TypedPrivilegedServer)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(TypedPrivilegedServer))
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .ToArray();

        stringEntryPoints.Should().BeEmpty(
            "the only privileged entry point is a typed, signed, allowlisted request — never a raw string");
    }
}
