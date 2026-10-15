using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Les ops de bail traversent tout le pipeline TypedPrivilegedServer :
//   (a) vérification HMAC → (b) nonce → (c) autorisation de l'appelant → (d) liaison codec →
//   (e) OpAllowlist.Validate → (f) routage vers le LeaseWatchdog (pas l'exécuteur privilégié :
//   établir ou renouveler un bail n'est pas en soi une écriture privilégiée).
//
// Le bail est lié à l'identité authentifiée de l'appelant (CallerInfo) : un appelant forgé
// ou non autorisé est refusé avant même de toucher le chien de garde, et un autre appelant
// authentifié ne peut ni renouveler ni détourner le bail d'un tiers.
// -----------------------------------------------------------------------
public sealed class LeaseIpcPipelineTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    private sealed class AllowAllAuth : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    private sealed class NoopExecutor : IPrivilegedOpExecutor
    {
        public bool WasInvoked { get; private set; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            WasInvoked = true;
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class RecordingDiagnostics : IDiagnosticsLog
    {
        public void Change(string tweakId, string from, string to, string actor) { }
        public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(string.Empty));
    }

    private static (TypedPrivilegedServer server, LeaseWatchdog wd, NoopExecutor exec, FakeClock clock) Build(byte[] key)
    {
        var clock = new FakeClock();
        var exec = new NoopExecutor();
        var wd = new LeaseWatchdog(clock, new OpAllowlist(), exec, new RecordingDiagnostics());
        var server = new TypedPrivilegedServer(key, new AllowAllAuth(), new OpAllowlist(), exec, leaseWatchdog: wd);
        return (server, wd, exec, clock);
    }

    private static TypedPrivilegedRequest SignedRequest(byte[] key, PrivilegedOp op)
    {
        var json = PrivilegedOpCodec.Serialize(op).Value!;
        var nonce = Guid.NewGuid().ToString("N");
        var hmac = TypedRequestHmac.Compute(key, json, nonce);
        return new TypedPrivilegedRequest { OpJson = json, Nonce = nonce, HmacBase64 = hmac };
    }

    private static string RevertJson() =>
        PrivilegedOpCodec.Serialize(new ApplyNvApiClockOffsetOp(0, 0, 0)).Value!;

    // --- une EstablishLeaseOp signée enregistre un bail qui revient en arrière tout seul plus tard ---

    [Fact]
    public async Task EstablishLease_via_pipeline_registers_lease_that_auto_reverts_at_expiry()
    {
        var key = NewKey();
        var (server, wd, exec, clock) = Build(key);
        var caller = new CallerInfo(4242);

        var resp = await server.ProcessTypedRequestAsync(
            SignedRequest(key, new EstablishLeaseOp("lease-1", RevertJson(), 30)), default, caller);

        resp.Ok.Should().BeTrue();
        exec.WasInvoked.Should().BeFalse("establishing a lease is not itself a privileged write");

        // Le faire expirer → le chien de garde joue le retour arrière enregistré via l'exécuteur.
        clock.Advance(TimeSpan.FromSeconds(31));
        await wd.TickAsync(default);
        exec.WasInvoked.Should().BeTrue("the watchdog must fire the recorded revert at expiry");
    }

    // --- une RenewLeaseOp signée par le même appelant prolonge le bail ---

    [Fact]
    public async Task RenewLease_via_pipeline_by_owner_extends_expiry()
    {
        var key = NewKey();
        var (server, wd, exec, clock) = Build(key);
        var owner = new CallerInfo(4242);

        (await server.ProcessTypedRequestAsync(
            SignedRequest(key, new EstablishLeaseOp("lease-1", RevertJson(), 30)), default, owner))
            .Ok.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(20));
        (await server.ProcessTypedRequestAsync(
            SignedRequest(key, new RenewLeaseOp("lease-1", 30)), default, owner))
            .Ok.Should().BeTrue();

        // t+40 aurait fait expirer le bail d'origine (t+30) ; renouvelé à t+50 → vivant.
        clock.Advance(TimeSpan.FromSeconds(20));
        await wd.TickAsync(default);
        exec.WasInvoked.Should().BeFalse("the renewed lease must not have fired");
    }

    // --- Sécurité : un autre appelant authentifié ne peut ni renouveler ni détourner un bail ---

    [Fact]
    public async Task RenewLease_via_pipeline_by_a_different_caller_is_denied()
    {
        var key = NewKey();
        var (server, wd, exec, clock) = Build(key);
        var owner = new CallerInfo(1111);
        var attacker = new CallerInfo(2222);

        (await server.ProcessTypedRequestAsync(
            SignedRequest(key, new EstablishLeaseOp("lease-1", RevertJson(), 30)), default, owner))
            .Ok.Should().BeTrue();

        // L'attaquant est un pair pleinement autorisé (AllowAllAuth) qui signe correctement,
        // mais il n'est pas le propriétaire du bail → le renouvellement doit être refusé.
        var hijack = await server.ProcessTypedRequestAsync(
            SignedRequest(key, new RenewLeaseOp("lease-1", 99999)), default, attacker);

        hijack.Ok.Should().BeFalse("a different caller must not renew another caller's lease");
        hijack.ErrorCode.Should().Be(ErrorCodes.LeaseUnknown);

        // Et le bail du vrai propriétaire expire toujours à son échéance réelle.
        clock.Advance(TimeSpan.FromSeconds(31));
        await wd.TickAsync(default);
        exec.WasInvoked.Should().BeTrue("the hijack must not have kept the owner's lease alive");
    }

    // --- Sécurité : un appelant forgé (non autorisé) est refusé avant le chien de garde ---

    [Fact]
    public async Task EstablishLease_with_unauthorized_caller_is_denied_before_watchdog()
    {
        var key = NewKey();
        var clock = new FakeClock();
        var exec = new NoopExecutor();
        var wd = new LeaseWatchdog(clock, new OpAllowlist(), exec, new RecordingDiagnostics());
        // Authentificateur fermé par défaut : l'étape (c) d'autorisation échoue.
        var server = new TypedPrivilegedServer(
            key, new DenyByDefaultCallerAuthenticator(), new OpAllowlist(), exec, leaseWatchdog: wd);

        var resp = await server.ProcessTypedRequestAsync(
            SignedRequest(key, new EstablishLeaseOp("lease-1", RevertJson(), 30)), default, new CallerInfo(4242));

        resp.Ok.Should().BeFalse();
        resp.ErrorCode.Should().Be(ErrorCodes.ElevationDenied);

        // Rien n'a été enregistré : même après l'échéance supposée, rien ne se déclenche.
        clock.Advance(TimeSpan.FromSeconds(120));
        await wd.TickAsync(default);
        exec.WasInvoked.Should().BeFalse("an unauthorized caller must never register a lease");
    }

    // --- Sécurité : une signature EstablishLease altérée est refusée ---

    [Fact]
    public async Task EstablishLease_with_tampered_signature_is_rejected()
    {
        var key = NewKey();
        var (server, wd, exec, clock) = Build(key);

        var req = SignedRequest(key, new EstablishLeaseOp("lease-1", RevertJson(), 30));
        var bytes = Convert.FromBase64String(req.HmacBase64);
        bytes[0] ^= 0xFF;
        var tampered = req with { HmacBase64 = Convert.ToBase64String(bytes) };

        var resp = await server.ProcessTypedRequestAsync(tampered, default, new CallerInfo(4242));

        resp.Ok.Should().BeFalse();
        resp.ErrorCode.Should().Be(ErrorCodes.HelperSignatureInvalid);

        clock.Advance(TimeSpan.FromSeconds(120));
        await wd.TickAsync(default);
        exec.WasInvoked.Should().BeFalse();
    }

    // --- une op de bail contre un serveur sans chien de garde échoue proprement (pas de plantage) ---

    [Fact]
    public async Task Lease_op_without_a_configured_watchdog_fails_clean()
    {
        var key = NewKey();
        var exec = new NoopExecutor();
        // Aucun chien de garde branché (hôte minimal) : l'op de bail doit échouer proprement.
        var server = new TypedPrivilegedServer(key, new AllowAllAuth(), new OpAllowlist(), exec);

        var resp = await server.ProcessTypedRequestAsync(
            SignedRequest(key, new EstablishLeaseOp("lease-1", RevertJson(), 30)), default, new CallerInfo(4242));

        resp.Ok.Should().BeFalse("a lease op needs a watchdog; without one it must fail clean, not crash");
    }

    // --- Le refus par défaut tient toujours : une op hors liste blanche reste refusée ---

    [Fact]
    public async Task Non_allowlisted_op_still_refused_with_watchdog_present()
    {
        var key = NewKey();
        var (server, _, exec, _) = Build(key);

        var evil = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend",
            "Start", RegistryValueType.DWord, "4");
        var resp = await server.ProcessTypedRequestAsync(
            SignedRequest(key, evil), default, new CallerInfo(4242));

        resp.Ok.Should().BeFalse();
        resp.ErrorCode.Should().Be(ErrorCodes.NotAllowlisted);
        exec.WasInvoked.Should().BeFalse();
    }
}
