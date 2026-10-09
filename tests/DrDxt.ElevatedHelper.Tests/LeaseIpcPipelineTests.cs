using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// R2 — the lease ops flow through the FULL TypedPrivilegedServer pipeline
//   (a) HMAC verify → (b) nonce → (c) caller authz → (d) codec bind →
//   (e) OpAllowlist.Validate → (f) route to the LeaseWatchdog (not the privileged
//   executor — establishing/renewing a lease is not itself a privileged write).
//
// The lease is tied to the AUTHENTICATED caller identity (CallerInfo): a forged /
// unauthorized caller is rejected before the watchdog is touched, and a DIFFERENT
// authenticated caller cannot renew or hijack another caller's lease.
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

    // --- a signed EstablishLeaseOp registers a lease that later auto-reverts ---

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

        // Expire it → the watchdog runs the recorded revert through the executor.
        clock.Advance(TimeSpan.FromSeconds(31));
        await wd.TickAsync(default);
        exec.WasInvoked.Should().BeTrue("the watchdog must fire the recorded revert at expiry");
    }

    // --- a signed RenewLeaseOp by the same caller extends the lease ---

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

        // t+40 would have expired the original (t+30) lease; renewed to t+50 → alive.
        clock.Advance(TimeSpan.FromSeconds(20));
        await wd.TickAsync(default);
        exec.WasInvoked.Should().BeFalse("the renewed lease must not have fired");
    }

    // --- SECURITY: a DIFFERENT authenticated caller cannot renew/hijack a lease ---

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

        // The attacker is a fully authorized peer (AllowAllAuth) and signs correctly,
        // but it is not the lease owner → the renew must be denied.
        var hijack = await server.ProcessTypedRequestAsync(
            SignedRequest(key, new RenewLeaseOp("lease-1", 99999)), default, attacker);

        hijack.Ok.Should().BeFalse("a different caller must not renew another caller's lease");
        hijack.ErrorCode.Should().Be(ErrorCodes.ElevationDenied);

        // And the original owner's lease still expires on its real schedule.
        clock.Advance(TimeSpan.FromSeconds(31));
        await wd.TickAsync(default);
        exec.WasInvoked.Should().BeTrue("the hijack must not have kept the owner's lease alive");
    }

    // --- SECURITY: a forged (unauthorized) caller is rejected before the watchdog ---

    [Fact]
    public async Task EstablishLease_with_unauthorized_caller_is_denied_before_watchdog()
    {
        var key = NewKey();
        var clock = new FakeClock();
        var exec = new NoopExecutor();
        var wd = new LeaseWatchdog(clock, new OpAllowlist(), exec, new RecordingDiagnostics());
        // Deny-by-default authenticator: the caller authz step (c) fails.
        var server = new TypedPrivilegedServer(
            key, new DenyByDefaultCallerAuthenticator(), new OpAllowlist(), exec, leaseWatchdog: wd);

        var resp = await server.ProcessTypedRequestAsync(
            SignedRequest(key, new EstablishLeaseOp("lease-1", RevertJson(), 30)), default, new CallerInfo(4242));

        resp.Ok.Should().BeFalse();
        resp.ErrorCode.Should().Be(ErrorCodes.ElevationDenied);

        // Nothing was registered: even after the would-be expiry, nothing fires.
        clock.Advance(TimeSpan.FromSeconds(120));
        await wd.TickAsync(default);
        exec.WasInvoked.Should().BeFalse("an unauthorized caller must never register a lease");
    }

    // --- SECURITY: a tampered EstablishLease signature is rejected ---

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

    // --- a lease op against a server without a watchdog fails clean (no crash) ---

    [Fact]
    public async Task Lease_op_without_a_configured_watchdog_fails_clean()
    {
        var key = NewKey();
        var exec = new NoopExecutor();
        // No watchdog wired (e.g. a minimal host): the lease op must fail clean.
        var server = new TypedPrivilegedServer(key, new AllowAllAuth(), new OpAllowlist(), exec);

        var resp = await server.ProcessTypedRequestAsync(
            SignedRequest(key, new EstablishLeaseOp("lease-1", RevertJson(), 30)), default, new CallerInfo(4242));

        resp.Ok.Should().BeFalse("a lease op needs a watchdog; without one it must fail clean, not crash");
    }

    // --- Deny-by-default still holds: a non-allowlisted op is still refused ---

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
