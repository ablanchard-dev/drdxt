using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// R2 (client side) — IElevatedClient.RenewLease + the reusable HeartbeatSender.
//
// RenewLease is a thin call that sends a RenewLeaseOp through the same ExecuteAsync
// IPC pipeline (so it inherits the full signature→nonce→authz→bind→allowlist path
// and the same caller identity). HeartbeatSender is a disposable that renews on a
// cadence until disposed; the held-offset path uses it to keep its lease alive.
//
// Tests drive the cadence deterministically (manual PulseAsync) — no real timers.
// -----------------------------------------------------------------------
public sealed class HeartbeatSenderTests
{
    // 08/10/2026 (revue n° 13) : un renouvellement refusé (le helper ne connaît plus le bail) se dit, une fois ; une
    // erreur passagère du transport ne se dit pas.
    private sealed class Renouvellement(params Result[] reponses) : IElevatedClient
    {
        private int _i;
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
            => Task.FromResult(reponses[Math.Min(_i++, reponses.Length - 1)]);
    }

    [Fact]
    public async Task Un_bail_inconnu_du_helper_leve_Perdu_une_fois()
    {
        var hb = new HeartbeatSender(new Renouvellement(
            Result.Fail(ErrorCodes.HelperOpFailed, "tube occupé"),
            Result.Fail(ErrorCodes.ElevationDenied, "No such lease for this caller.")), "gpu-expert", 120);
        var perdus = 0;
        hb.Perdu += () => perdus++;

        await hb.PulseAsync(CancellationToken.None);
        perdus.Should().Be(0, "une erreur passagère n'est pas un bail perdu");
        await hb.PulseAsync(CancellationToken.None);
        await hb.PulseAsync(CancellationToken.None);
        perdus.Should().Be(1);
        await hb.DisposeAsync();
    }

    // Autocontrôle du 08/10/2026 (SEC1) : un essai rendu laissait son bail vivre jusqu'à expiration (2 min), et le helper
    // attendait avec lui au lieu de s'arrêter à la fermeture de l'App. Libérer = plus de battement, bail ramené à 1 s.
    private sealed class Enregistreur : IElevatedClient
    {
        public List<PrivilegedOp> Ops { get; } = [];
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) { Ops.Add(op); return Task.FromResult(Result.Ok()); }
    }

    [Fact]
    public async Task Liberer_ramene_le_bail_a_une_seconde_et_arrete_le_battement()
    {
        var client = new Enregistreur();
        var hb = new HeartbeatSender(client, "gpu-ventilateur", 120);

        await hb.LibererAsync();
        await hb.PulseAsync(CancellationToken.None);

        client.Ops.Should().Equal(new RenewLeaseOp("gpu-ventilateur", 1));
    }

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    private sealed class AllowAllAuth : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    private sealed class NoopExecutor : IPrivilegedOpExecutor
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) => Task.FromResult(Result.Ok());
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

    /// <summary>Counts RenewLeaseOps it is asked to send, and lets a test simulate a
    /// transient failure.</summary>
    private sealed class CountingClient : IElevatedClient
    {
        public int RenewCount { get; private set; }
        public string? LastLeaseId { get; private set; }
        public Func<Result>? NextResult { get; set; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (op is RenewLeaseOp r)
            {
                RenewCount++;
                LastLeaseId = r.LeaseId;
            }
            return Task.FromResult(NextResult?.Invoke() ?? Result.Ok());
        }
    }

    // --- RenewLease sends a RenewLeaseOp through ExecuteAsync (same pipeline) ---

    [Fact]
    public async Task RenewLease_sends_a_RenewLeaseOp_via_ExecuteAsync()
    {
        var counting = new CountingClient();
        IElevatedClient client = counting; // RenewLease is the interface contract

        var r = await client.RenewLease("lease-1", leaseSeconds: 30, default);

        r.IsOk.Should().BeTrue();
        counting.RenewCount.Should().Be(1);
        counting.LastLeaseId.Should().Be("lease-1");
    }

    [Fact]
    public async Task RenewLease_roundtrips_through_the_full_server_pipeline_for_the_owner()
    {
        // End-to-end: a real client → loopback → real server+watchdog. Establish a
        // lease first (owner identity), then renew via the client and confirm it
        // extended the lease (the watchdog does not fire after the renewed expiry).
        var key = NewKey();
        var clock = new FakeClock();
        var exec = new NoopExecutor();
        var wd = new LeaseWatchdog(clock, new OpAllowlist(), new RecordingTracker(out var tracker), new RecordingDiagnostics());
        var server = new TypedPrivilegedServer(key, new AllowAllAuth(), new OpAllowlist(), exec, leaseWatchdog: wd);
        IElevatedClient client = new TypedNamedPipeElevatedClient(key, new LoopbackTransport(server, new CallerInfo(7)));

        var revert = PrivilegedOpCodec.Serialize(new ApplyNvApiClockOffsetOp(0, 0, 0)).Value!;
        (await client.ExecuteAsync(new EstablishLeaseOp("lease-1", revert, 30), default)).IsOk.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(20));
        (await client.RenewLease("lease-1", 30, default)).IsOk.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(20)); // t+40, renewed to t+50 → alive
        await wd.TickAsync(default);
        tracker.Fired.Should().Be(0, "the renewed lease must not have auto-reverted");
    }

    // --- HeartbeatSender renews on each pulse until disposed ---

    [Fact]
    public async Task HeartbeatSender_renews_on_each_pulse()
    {
        var client = new CountingClient();
        await using var hb = new HeartbeatSender(client, "lease-1", leaseSeconds: 30);

        await hb.PulseAsync(default);
        await hb.PulseAsync(default);
        await hb.PulseAsync(default);

        client.RenewCount.Should().Be(3);
        client.LastLeaseId.Should().Be("lease-1");
    }

    [Fact]
    public async Task HeartbeatSender_stops_renewing_after_dispose()
    {
        var client = new CountingClient();
        var hb = new HeartbeatSender(client, "lease-1", leaseSeconds: 30);

        await hb.PulseAsync(default);
        await hb.DisposeAsync();

        // A pulse after disposal is a no-op (does not renew).
        await hb.PulseAsync(default);

        client.RenewCount.Should().Be(1, "a disposed heartbeat must not keep renewing");
    }

    [Fact]
    public async Task HeartbeatSender_pulse_swallows_a_transient_renew_failure_no_throw()
    {
        var client = new CountingClient { NextResult = () => Result.Fail(ErrorCodes.IoFailure, "pipe busy") };
        await using var hb = new HeartbeatSender(client, "lease-1", leaseSeconds: 30);

        var act = async () => await hb.PulseAsync(default);

        await act.Should().NotThrowAsync("a transient renew failure must not crash the heartbeat");
        client.RenewCount.Should().Be(1);
    }

    // --- background loop: a real (short) interval actually renews, then stops on dispose ---

    [Fact]
    public async Task HeartbeatSender_background_loop_renews_then_stops_on_dispose()
    {
        var client = new CountingClient();
        var hb = new HeartbeatSender(client, "lease-1", leaseSeconds: 30,
            interval: TimeSpan.FromMilliseconds(15));

        hb.Start();
        // This waits on a condition, not on a duration: the poll exits on the first renew.
        // The timeout is a failure bound, so raising it costs nothing on a healthy run.
        // 2s was not enough on a contended CI runner (2026-07-01: this was the only red test
        // out of 333 in this project). 15 ms interval, 10 s of headroom.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(10);
        while (client.RenewCount == 0 && waited.Elapsed < timeout)
            await Task.Delay(10);

        client.RenewCount.Should().BeGreaterThan(0,
            "the background loop must renew on its interval (waited {0} ms for a 15 ms interval)",
            waited.ElapsedMilliseconds);

        await hb.DisposeAsync();
        var countAtDispose = client.RenewCount;
        await Task.Delay(80); // longer than several intervals
        client.RenewCount.Should().Be(countAtDispose, "the loop must stop after dispose");
    }

    // --- helpers ---------------------------------------------------------

    private sealed class LoopbackTransport : ITypedRequestTransport
    {
        private readonly TypedPrivilegedServer _server;
        private readonly CallerInfo _caller;
        public LoopbackTransport(TypedPrivilegedServer server, CallerInfo caller)
        {
            _server = server;
            _caller = caller;
        }
        public Task<PrivilegedResponse> SendAsync(TypedPrivilegedRequest request, CancellationToken ct)
            => _server.ProcessTypedRequestAsync(request, ct, _caller);
    }

    /// <summary>An executor that just counts how many times it ran a revert.</summary>
    private sealed class RecordingTracker : IPrivilegedOpExecutor
    {
        private readonly Tracker _t;
        public RecordingTracker(out Tracker tracker) { _t = new Tracker(); tracker = _t; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            _t.Fired++;
            return Task.FromResult(Result.Ok());
        }
    }

    internal sealed class Tracker { public int Fired; }
}
