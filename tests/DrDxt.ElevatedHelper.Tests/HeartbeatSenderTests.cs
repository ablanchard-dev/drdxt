using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// IElevatedClient.RenewLease et le HeartbeatSender réutilisable.
//
// RenewLease est un appel mince qui envoie une RenewLeaseOp par le même pipeline IPC
// ExecuteAsync (il hérite donc du chemin complet signature → nonce → autorisation → liaison →
// liste blanche, et de la même identité d'appelant). HeartbeatSender est un jetable qui
// renouvelle à cadence fixe jusqu'à sa libération ; le décalage tenu s'en sert pour garder son bail en vie.
//
// Les tests pilotent la cadence à la main (PulseAsync) : aucun vrai minuteur.
// -----------------------------------------------------------------------
public sealed class HeartbeatSenderTests
{
    // Un renouvellement refusé (le helper ne connaît plus le bail) se dit, une fois ; une
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
            Result.Fail(ErrorCodes.LeaseReverted, "The helper reverted this lease.")), "gpu-expert", 120);
        var perdus = 0;
        hb.Perdu += () => perdus++;

        await hb.PulseAsync(CancellationToken.None);
        perdus.Should().Be(0, "une erreur passagère n'est pas un bail perdu");
        await hb.PulseAsync(CancellationToken.None);
        await hb.PulseAsync(CancellationToken.None);
        perdus.Should().Be(1);
        await hb.DisposeAsync();
    }

    // « Caller not authorized » et les refus de poignée de main sortent aussi en ElevationDenied, passagers : les prendre
    // pour un bail perdu faisait dire « rendu par le helper » pendant que le battement continuait et gardait le décalage.
    [Fact]
    public async Task Un_refus_d_appelant_passager_ne_leve_pas_Perdu()
    {
        var hb = new HeartbeatSender(new Renouvellement(Result.Fail(ErrorCodes.ElevationDenied, "Caller not authorized.")), "gpu-expert", 120);
        var perdus = 0;
        hb.Perdu += () => perdus++;

        await hb.PulseAsync(CancellationToken.None);

        perdus.Should().Be(0);
        await hb.DisposeAsync();
    }

    // Helper relancé (registre vide) : le bail est inconnu, pas rendu ; le réglage est peut-être encore posé. Pas de
    // Perdu (le journal reste pour le démarrage suivant), mais plus de battement inutile.
    [Fact]
    public async Task Un_bail_inconnu_arrete_le_battement_sans_lever_Perdu()
    {
        var client = new CountingClient { NextResult = () => Result.Fail(ErrorCodes.LeaseUnknown, "No such lease for this caller.") };
        var hb = new HeartbeatSender(client, "gpu-expert", 120);
        var perdus = 0;
        hb.Perdu += () => perdus++;

        await hb.PulseAsync(CancellationToken.None);
        await hb.PulseAsync(CancellationToken.None);

        perdus.Should().Be(0);
        client.RenewCount.Should().Be(1);
        await hb.DisposeAsync();
    }

    // Le battement croise l'inverse du helper : passager. Le battement suivant lit « rendu » et lève Perdu.
    [Fact]
    public async Task Un_inverse_en_cours_ne_coupe_pas_le_battement()
    {
        var reponses = new Queue<Result>([
            Result.Fail(ErrorCodes.LeaseReverting, "inverse running"),
            Result.Fail(ErrorCodes.LeaseReverted, "The helper reverted this lease."),
        ]);
        var client = new CountingClient { NextResult = () => reponses.Dequeue() };
        var hb = new HeartbeatSender(client, "gpu-expert", 120);
        var perdus = 0;
        hb.Perdu += () => perdus++;

        await hb.PulseAsync(CancellationToken.None);
        await hb.PulseAsync(CancellationToken.None);

        perdus.Should().Be(1);
        await hb.DisposeAsync();
    }

    // Le bail perdu, plus rien à garder : aucun renouvellement de plus (un battement orphelin envoyait un IPC refusé toutes
    // les 40 s jusqu'à la fermeture).
    [Fact]
    public async Task Apres_Perdu_plus_aucun_renouvellement()
    {
        var client = new CountingClient { NextResult = () => Result.Fail(ErrorCodes.LeaseReverted, "The helper reverted this lease.") };
        var hb = new HeartbeatSender(client, "gpu-expert", 120);

        await hb.PulseAsync(CancellationToken.None);
        await hb.PulseAsync(CancellationToken.None);

        client.RenewCount.Should().Be(1);
        await hb.DisposeAsync();
    }

    // Un essai rendu laissait son bail vivre jusqu'à expiration (2 min), et le helper
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

    /// <summary>Compte les RenewLeaseOp qu'on lui demande d'envoyer et laisse un test simuler
    /// une panne passagère.</summary>
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

    // --- RenewLease envoie une RenewLeaseOp par ExecuteAsync (même pipeline) ---

    [Fact]
    public async Task RenewLease_sends_a_RenewLeaseOp_via_ExecuteAsync()
    {
        var counting = new CountingClient();
        IElevatedClient client = counting; // RenewLease fait partie du contrat de l'interface

        var r = await client.RenewLease("lease-1", leaseSeconds: 30, default);

        r.IsOk.Should().BeTrue();
        counting.RenewCount.Should().Be(1);
        counting.LastLeaseId.Should().Be("lease-1");
    }

    [Fact]
    public async Task RenewLease_roundtrips_through_the_full_server_pipeline_for_the_owner()
    {
        // De bout en bout : vrai client → boucle locale → vrai serveur + chien de garde. Établir
        // d'abord un bail (identité du propriétaire), puis renouveler via le client et vérifier
        // qu'il a prolongé le bail (le chien de garde ne se déclenche pas après l'échéance renouvelée).
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

        clock.Advance(TimeSpan.FromSeconds(20)); // t+40, renouvelé à t+50 → vivant
        await wd.TickAsync(default);
        tracker.Fired.Should().Be(0, "the renewed lease must not have auto-reverted");
    }

    // --- HeartbeatSender renouvelle à chaque pulsation jusqu'à sa libération ---

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

        // Une pulsation après libération ne fait rien (ne renouvelle pas).
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

    // --- boucle de fond : un vrai intervalle (court) renouvelle bien, puis s'arrête à la libération ---

    [Fact]
    public async Task HeartbeatSender_background_loop_renews_then_stops_on_dispose()
    {
        var client = new CountingClient();
        var hb = new HeartbeatSender(client, "lease-1", leaseSeconds: 30,
            interval: TimeSpan.FromMilliseconds(15));

        hb.Start();
        // On attend une condition, pas une durée : la scrutation sort au premier renouvellement.
        // Le délai est une borne d'échec ; le monter ne coûte rien sur une exécution saine.
        // 2 s ne suffisaient pas sur un runner CI saturé. Intervalle de 15 ms, 10 s de marge.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(10);
        while (client.RenewCount == 0 && waited.Elapsed < timeout)
            await Task.Delay(10);

        client.RenewCount.Should().BeGreaterThan(0,
            "the background loop must renew on its interval (waited {0} ms for a 15 ms interval)",
            waited.ElapsedMilliseconds);

        await hb.DisposeAsync();
        var countAtDispose = client.RenewCount;
        await Task.Delay(80); // plus long que plusieurs intervalles
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

    /// <summary>Exécuteur qui compte seulement combien de fois il a joué un retour arrière.</summary>
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

    // Le battement naît pendant la pose, mais vit après elle : il ne voit jamais la portée qui entourait son lancement.
    private sealed class PorteeVue : IElevatedClient
    {
        public readonly TaskCompletionSource<PorteeDePose?> Premier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Premier.TrySetResult(PorteeDePose.Courante);
            return Task.FromResult(Result.Ok());
        }
    }

    [Fact]
    public async Task Un_battement_lance_dans_une_portee_de_pose_n_en_herite_pas()
    {
        var client = new PorteeVue();
        var hb = new HeartbeatSender(client, "gpu-expert", 120, TimeSpan.FromMilliseconds(10));
        PorteeDePose.Ouvrir();

        hb.Start();
        var vue = await client.Premier.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await hb.DisposeAsync();

        PorteeDePose.Courante.Should().NotBeNull("la portée reste ouverte pour la pose elle-même");
        vue.Should().BeNull();
    }
}
