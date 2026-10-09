using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using DrDxt.ElevatedHelper.Leases;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// R2 — LeaseWatchdog: the helper-side dead-man's-switch.
//
// A held/reversible tweak (GPU clock offset / power limit) registers a lease
// carrying the existing typed revert op (as codec JSON) + an expiry. A background
// monitor ticks; on expiry it re-binds + re-validates the revert through the same
// bounded codec + allowlist and runs it via the existing executor, then clears the
// lease. Renew (heartbeat) extends expiry; only the OWNING caller may renew. Time
// is an INJECTED clock so expiry is deterministic with NO real waiting.
//
// SECURITY invariants pinned here:
//   * the revert is an existing typed op, re-validated leaf-exact at expiry —
//     never a new generic privileged primitive;
//   * leases are tied to the AUTHENTICATED caller identity; a different caller
//     cannot renew/complete/hijack another caller's lease (deny-by-default);
//   * revert failure at expiry is logged + retried, never throws.
// -----------------------------------------------------------------------
public sealed class LeaseWatchdogTests
{
    // --- test doubles -----------------------------------------------------

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>Records every op the watchdog asks the executor to run.</summary>
    private sealed class RecordingExecutor : IPrivilegedOpExecutor
    {
        public List<PrivilegedOp> Executed { get; } = new();
        private readonly Func<PrivilegedOp, Result> _resultFor;
        public RecordingExecutor(Func<PrivilegedOp, Result>? resultFor = null)
            => _resultFor = resultFor ?? (_ => Result.Ok());

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Executed.Add(op);
            return Task.FromResult(_resultFor(op));
        }
    }

    private sealed class RecordingDiagnostics : IDiagnosticsLog
    {
        public List<(string tweakId, string from, string to, string actor)> Entries { get; } = new();
        public void Change(string tweakId, string from, string to, string actor)
            => Entries.Add((tweakId, from, to, actor));
        public Task<Result<string>> ExportAsync() => Task.FromResult(Result<string>.Ok(string.Empty));
    }

    private static readonly LeaseOwner OwnerA = new(1111);
    private static readonly LeaseOwner OwnerB = new(2222);

    // A real, allowlisted held-tweak inverse: restore stock (0 MHz) core offset.
    private static string RevertJson() =>
        PrivilegedOpCodec.Serialize(new ApplyNvApiClockOffsetOp(0, 0, 0)).Value!;

    private static LeaseWatchdog Build(
        IClock clock, IPrivilegedOpExecutor exec, IDiagnosticsLog? log = null) =>
        new(clock, new OpAllowlist(), exec, log ?? new RecordingDiagnostics());

    // --- establish + expiry fires the recorded revert ---------------------

    [Fact]
    public async Task Expired_lease_fires_recorded_revert_then_clears()
    {
        var clock = new FakeClock();
        var exec = new RecordingExecutor();
        var wd = Build(clock, exec);

        var est = wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30);
        est.IsOk.Should().BeTrue();

        // Not yet expired → tick fires nothing.
        clock.Advance(TimeSpan.FromSeconds(20));
        await wd.TickAsync(default);
        exec.Executed.Should().BeEmpty("the lease has not expired yet");

        // Past expiry → the recorded inverse runs, exactly once.
        clock.Advance(TimeSpan.FromSeconds(15));
        await wd.TickAsync(default);
        exec.Executed.Should().ContainSingle().Which.Should().BeOfType<ApplyNvApiClockOffsetOp>();

        // Cleared: a second tick does not fire it again.
        clock.Advance(TimeSpan.FromSeconds(60));
        await wd.TickAsync(default);
        exec.Executed.Should().HaveCount(1, "an expired+reverted lease must be cleared, never re-fired");
    }

    // --- renew (heartbeat) pushes expiry out so the revert does not fire ---

    [Fact]
    public async Task Renew_extends_expiry_so_revert_does_not_fire()
    {
        var clock = new FakeClock();
        var exec = new RecordingExecutor();
        var wd = Build(clock, exec);

        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();

        // Heartbeat at t+20 extends expiry to t+20+30 = t+50.
        clock.Advance(TimeSpan.FromSeconds(20));
        wd.Renew(OwnerA, "lease-1", leaseSeconds: 30).IsOk.Should().BeTrue();

        // At t+40 (would have expired at t+30) the renewed lease is still alive.
        clock.Advance(TimeSpan.FromSeconds(20));
        await wd.TickAsync(default);
        exec.Executed.Should().BeEmpty("a renewed lease must not fire its revert");

        // Past the renewed expiry → it fires.
        clock.Advance(TimeSpan.FromSeconds(20)); // now t+60 > t+50
        await wd.TickAsync(default);
        exec.Executed.Should().ContainSingle();
    }

    // --- explicit complete/clear: the controller reverted cleanly itself ---

    [Fact]
    public async Task Complete_clears_lease_so_watchdog_never_fires()
    {
        var clock = new FakeClock();
        var exec = new RecordingExecutor();
        var wd = Build(clock, exec);

        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();
        wd.Complete(OwnerA, "lease-1").IsOk.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(120));
        await wd.TickAsync(default);
        exec.Executed.Should().BeEmpty("a completed lease must never auto-revert");
    }

    // --- SECURITY: a different caller cannot renew/complete another's lease ---

    [Fact]
    public void Renew_by_a_different_caller_is_denied()
    {
        var clock = new FakeClock();
        var wd = Build(clock, new RecordingExecutor());

        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();

        var hijack = wd.Renew(OwnerB, "lease-1", leaseSeconds: 9999);

        hijack.IsOk.Should().BeFalse("only the owning caller may renew its lease");
        hijack.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public async Task Hijack_renew_does_not_keep_the_real_owners_lease_alive()
    {
        var clock = new FakeClock();
        var exec = new RecordingExecutor();
        var wd = Build(clock, exec);

        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();

        // Attacker tries to keep it alive forever — must be rejected and have NO effect.
        wd.Renew(OwnerB, "lease-1", leaseSeconds: 9999).IsOk.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(31));
        await wd.TickAsync(default);
        exec.Executed.Should().ContainSingle("a denied hijack-renew must not have extended the lease");
    }

    [Fact]
    public void Complete_by_a_different_caller_is_denied()
    {
        var clock = new FakeClock();
        var wd = Build(clock, new RecordingExecutor());
        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();

        var r = wd.Complete(OwnerB, "lease-1");

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    // --- SECURITY: deny-by-default — a missing/unknown owner cannot operate ---

    [Fact]
    public void Establish_with_an_unidentified_owner_is_denied()
    {
        var clock = new FakeClock();
        var wd = Build(clock, new RecordingExecutor());

        // LeaseOwner with no resolved pid = not authenticated → deny.
        var r = wd.Establish(default, "lease-1", RevertJson(), leaseSeconds: 30);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void Renew_an_unknown_lease_is_denied_not_silently_created()
    {
        var clock = new FakeClock();
        var wd = Build(clock, new RecordingExecutor());

        var r = wd.Renew(OwnerA, "no-such-lease", leaseSeconds: 30);

        r.IsOk.Should().BeFalse("renewing a lease that was never established must fail, never create one");
    }

    // --- SECURITY: the revert op must itself pass the allowlist at register time ---

    [Fact]
    public void Establish_with_a_non_allowlisted_revert_is_refused()
    {
        var clock = new FakeClock();
        var wd = Build(clock, new RecordingExecutor());

        // A perfectly-typed but NON-allowlisted op (targets Defender) as the "revert".
        var evil = PrivilegedOpCodec.Serialize(new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend",
            "Start", RegistryValueType.DWord, "4")).Value!;

        var r = wd.Establish(OwnerA, "lease-1", evil, leaseSeconds: 30);

        r.IsOk.Should().BeFalse("the recorded revert must pass the leaf-exact allowlist, like any op");
        r.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void Establish_with_a_malformed_revert_payload_is_refused_no_throw()
    {
        var clock = new FakeClock();
        var wd = Build(clock, new RecordingExecutor());

        var act = () => wd.Establish(OwnerA, "lease-1", "{ not a valid op", leaseSeconds: 30);

        var r = act.Should().NotThrow().Subject;
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void Establish_with_a_non_positive_lease_is_refused()
    {
        var clock = new FakeClock();
        var wd = Build(clock, new RecordingExecutor());

        wd.Establish(OwnerA, "l", RevertJson(), leaseSeconds: 0).IsOk.Should().BeFalse();
        wd.Establish(OwnerA, "l", RevertJson(), leaseSeconds: -5).IsOk.Should().BeFalse();
    }

    // --- revert FAILURE at expiry: logged + retried, never throws, never cleared ---

    [Fact]
    public async Task Revert_failure_at_expiry_is_logged_and_retried_until_it_succeeds()
    {
        var clock = new FakeClock();
        var attempts = 0;
        var exec = new RecordingExecutor(_ =>
        {
            attempts++;
            // Fail the first attempt, succeed the second (best-effort retry).
            return attempts == 1
                ? Result.Fail(ErrorCodes.HelperOpFailed, "SetPstates20 transient error")
                : Result.Ok();
        });
        var log = new RecordingDiagnostics();
        var wd = Build(clock, exec, log);

        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(31));

        // First tick: revert fails → logged, lease not cleared.
        await wd.TickAsync(default);
        attempts.Should().Be(1);
        log.Entries.Should().NotBeEmpty("a failed auto-revert must be journaled");

        // Second tick: still expired → retried, this time succeeds → cleared.
        await wd.TickAsync(default);
        attempts.Should().Be(2);

        // Third tick: cleared, no further attempts.
        await wd.TickAsync(default);
        attempts.Should().Be(2, "a successfully-reverted lease must be cleared");
    }

    [Fact]
    public async Task A_throwing_executor_at_expiry_never_propagates()
    {
        var clock = new FakeClock();
        var exec = new ThrowingExecutor();
        var wd = Build(clock, exec);

        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(31));

        var act = async () => await wd.TickAsync(default);
        await act.Should().NotThrowAsync("a throwing executor must never cross the watchdog/IPC boundary");
    }

    private sealed class ThrowingExecutor : IPrivilegedOpExecutor
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    /// <summary>Leve au premier appel (l'exception donnee), reussit ensuite.</summary>
    private sealed class FirstCallThrowsExecutor(Exception first) : IPrivilegedOpExecutor
    {
        public int Calls { get; private set; }
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
            => ++Calls == 1 ? throw first : Task.FromResult(Result.Ok());
    }

    // 26/09/2026 : trois chemins que des mutations laissaient verts.

    [Fact]
    public async Task A_lease_never_fires_before_its_expiry()
    {
        // Un bail qui part en avance retire le plafond d'envoi en pleine partie, application vivante.
        var clock = new FakeClock();
        var exec = new RecordingExecutor();
        var wd = Build(clock, exec);
        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(29));
        await wd.TickAsync(default);
        exec.Executed.Should().BeEmpty("a 29 s, le bail de 30 s court encore");

        clock.Advance(TimeSpan.FromSeconds(2));
        await wd.TickAsync(default);
        exec.Executed.Should().ContainSingle();
    }

    [Fact]
    public async Task A_throwing_revert_is_retried_on_the_next_tick()
    {
        var clock = new FakeClock();
        var exec = new FirstCallThrowsExecutor(new InvalidOperationException("boom"));
        var wd = Build(clock, exec);
        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(31));

        await wd.TickAsync(default);
        await wd.TickAsync(default);
        exec.Calls.Should().Be(2, "le bail est remis au registre pour que l'inverse soit retente");
    }

    [Fact]
    public async Task A_cancelled_revert_is_retried_on_the_next_tick()
    {
        var clock = new FakeClock();
        var exec = new FirstCallThrowsExecutor(new OperationCanceledException());
        var wd = Build(clock, exec);
        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(31));

        var annule = async () => await wd.TickAsync(default);
        await annule.Should().ThrowAsync<OperationCanceledException>("l'annulation remonte, ce n'est pas un echec");
        await wd.TickAsync(default);
        exec.Calls.Should().Be(2, "le bail est remis au registre : l'inverse n'est pas perdu");
    }

    // --- two owners, independent leases ----------------------------------

    [Fact]
    public async Task Leases_are_isolated_per_owner_and_id()
    {
        var clock = new FakeClock();
        var exec = new RecordingExecutor();
        var wd = Build(clock, exec);

        // REV6b (09/10/2026) : un nom de bail désigne un réglage ; deux clients ne tiennent plus le même nom en même temps
        // (InverseEnCoursTests). L'indépendance des expirations se prouve sur deux noms.
        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();
        wd.Establish(OwnerB, "lease-2", RevertJson(), leaseSeconds: 90).IsOk.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(31)); // A expired, B not
        await wd.TickAsync(default);
        exec.Executed.Should().ContainSingle("only owner A's lease has expired");

        clock.Advance(TimeSpan.FromSeconds(60)); // B now expired too
        await wd.TickAsync(default);
        exec.Executed.Should().HaveCount(2);
    }

    [Fact]
    public void A_firmware_reboot_can_NEVER_be_a_lease_revert()
    {
        // Breche trouvee en revue le 26/08. L'allowlist autorise RebootToFirmwareSetupOp --
        // a juste titre, pour un clic utilisateur. Mais le registre de baux valide le revert
        // avec la meme allowlist, et le declencheur normal d'un dead-man's switch est la MORT
        // du controleur : le revert part donc precisement quand plus personne n'est devant la
        // machine. Elle repartait dans le setup firmware sans rien a l'ecran pour l'expliquer.
        //
        // C'est le meme invariant que l'omission de la couture cote ReconcilerHost, perce par
        // une autre porte -- et le commentaire de Program.cs le declarait impossible.
        var wd = Build(new FakeClock(), new RecordingExecutor());

        var reboot = PrivilegedOpCodec.Serialize(new RebootToFirmwareSetupOp(0)).Value!;
        var cancel = PrivilegedOpCodec.Serialize(new CancelPendingFirmwareBootOp()).Value!;

        wd.Establish(OwnerA, "lease-fw", reboot, leaseSeconds: 30).IsOk.Should().BeFalse();
        wd.Establish(OwnerA, "lease-fw2", cancel, leaseSeconds: 30).IsOk.Should().BeFalse();

        // Et l'op reste parfaitement valide par ailleurs : c'est le BAIL qui la refuse,
        // pas l'allowlist -- sinon on aurait casse le clic utilisateur.
        new OpAllowlist().Validate(new RebootToFirmwareSetupOp(0)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Une_ecriture_BCD_ne_peut_jamais_etre_le_revert_d_un_bail()
    {
        var wd = Build(new FakeClock(), new RecordingExecutor());

        var poser = PrivilegedOpCodec.Serialize(new SetBcdValueOp("bootstatuspolicy", "IgnoreAllFailures")).Value!;
        var retirer = PrivilegedOpCodec.Serialize(new DeleteBcdValueOp("bootstatuspolicy")).Value!;

        wd.Establish(OwnerA, "lease-bcd", poser, leaseSeconds: 30).IsOk.Should().BeFalse();
        wd.Establish(OwnerA, "lease-bcd2", retirer, leaseSeconds: 30).IsOk.Should().BeFalse();

        // Le geste du joueur reste permis : c'est le BAIL qui refuse, pas l'allowlist.
        new OpAllowlist().Validate(new DeleteBcdValueOp("bootstatuspolicy")).IsOk.Should().BeTrue();
    }

    [Fact]
    public async Task Un_renouvellement_pendant_l_inverse_d_un_bail_expire_n_est_pas_dit_accepte()
    {
        // 25/09/2026 : un Renew arrivé pendant l'exécution de l'inverse voyait encore le bail et répondait
        // « Ok », puis l'inverse aboutissait et supprimait l'entrée renouvelée : l'appli croyait son bail
        // vivant. Le bail quitte le registre avant l'inverse : le Renew est refusé, l'appli repose (état connu).
        var clock = new FakeClock();
        LeaseWatchdog? wd = null;
        Result? pendantLInverse = null;
        var exec = new RecordingExecutor(_ =>
        {
            pendantLInverse = wd!.Renew(OwnerA, "lease-1", 30);
            return Result.Ok();
        });
        wd = Build(clock, exec);
        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(35));
        await wd.TickAsync(default);

        exec.Executed.Should().ContainSingle("l'inverse a bien tourné");
        pendantLInverse!.IsOk.Should().BeFalse("le bail est en train d'être défait : dire « Ok » serait faux");
        wd.Renew(OwnerA, "lease-1", 30).IsOk.Should().BeFalse("et il n'existe plus après");
    }

    [Fact]
    public async Task Un_inverse_qui_echoue_remet_le_bail_pour_le_passage_suivant()
    {
        var clock = new FakeClock();
        var exec = new RecordingExecutor(_ => Result.Fail(ErrorCodes.TweakNotApplicable, "pilote occupé"));
        var wd = Build(clock, exec);
        wd.Establish(OwnerA, "lease-1", RevertJson(), leaseSeconds: 30).IsOk.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(35));
        await wd.TickAsync(default);
        await wd.TickAsync(default);

        exec.Executed.Should().HaveCount(2, "l'échec garde le bail : le passage suivant réessaie");
    }

    // 26/09/2026 : le chien de garde vit en memoire. Quand le helper s'arretait (arret normal, ou rafale
    // d'echecs provoquee par un squatteur du tube), les baux encore actifs mouraient avec lui : le plafond
    // d'envoi pose pour une partie restait jusqu'au redemarrage. A l'arret, tout bail actif est rendu.
    [Fact]
    public async Task A_l_arret_du_helper_tout_bail_actif_est_rendu_meme_non_expire()
    {
        var clock = new FakeClock();
        var exec = new RecordingExecutor();
        var wd = Build(clock, exec);
        wd.Establish(OwnerA, "bail-1", RevertJson(), leaseSeconds: 300).IsOk.Should().BeTrue();
        wd.Establish(OwnerB, "bail-2", RevertJson(), leaseSeconds: 300).IsOk.Should().BeTrue();

        await wd.RendreToutAsync(default);

        exec.Executed.Should().HaveCount(2, "aucun bail n'etait expire, mais plus personne ne pourra le rendre");
        await wd.TickAsync(default);
        exec.Executed.Should().HaveCount(2, "rendus, ils ne se rejouent pas");
    }

    // 26/09/2026 : un bail dont l'inverse est un ordre de bail passait l'allowlist. A l'expiration,
    // l'executeur le refuse (« Unsupported »), le bail est remis, et tout recommence toutes les 5 s.
    [Fact]
    public void Un_ordre_de_bail_ne_peut_pas_etre_l_inverse_d_un_bail()
    {
        var wd = Build(new FakeClock(), new RecordingExecutor());
        var bailEnInverse = PrivilegedOpCodec.Serialize(new EstablishLeaseOp("x", RevertJson(), 30)).Value!;
        var renouvEnInverse = PrivilegedOpCodec.Serialize(new RenewLeaseOp("x", 30)).Value!;

        wd.Establish(OwnerA, "l", bailEnInverse, 30).IsOk.Should().BeFalse();
        wd.Establish(OwnerA, "l", renouvEnInverse, 30).IsOk.Should().BeFalse();
        wd.Establish(OwnerA, "l", RevertJson(), 30).IsOk.Should().BeTrue("temoin : un vrai inverse passe");
    }

    // 26/09/2026 : un inverse qui echoue est rejoue a chaque passage (il le faut : abandonner laisserait
    // l'etat en place), mais chaque echec ecrivait une ligne dans un journal sans borne, toutes les 5 s.
    [Fact]
    public async Task Un_echec_qui_se_repete_ne_s_ecrit_qu_une_fois_par_erreur()
    {
        var clock = new FakeClock();
        var code = ErrorCodes.HelperOpFailed;
        var exec = new RecordingExecutor(_ => Result.Fail(code, "non"));
        var log = new RecordingDiagnostics();
        var wd = Build(clock, exec, log);
        wd.Establish(OwnerA, "l", RevertJson(), 30);
        clock.Advance(TimeSpan.FromSeconds(31));

        for (var i = 0; i < 5; i++) await wd.TickAsync(default);
        exec.Executed.Should().HaveCount(5, "l'inverse est rejoue a chaque passage");
        log.Entries.Should().ContainSingle(e => e.to.StartsWith("revert-failed"));

        code = ErrorCodes.NotAllowlisted;
        await wd.TickAsync(default);
        log.Entries.Count(e => e.to.StartsWith("revert-failed")).Should().Be(2, "une erreur nouvelle s'ecrit");
    }
}
