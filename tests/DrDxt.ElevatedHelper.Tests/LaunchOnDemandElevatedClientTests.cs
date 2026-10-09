using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// LaunchOnDemandElevatedClient — defers the UAC launch to the first privileged op
// (so the App does not prompt at startup), then delegates to the real
// handshaking client. Behind seams: an ensure-running delegate + an inner client.
//
//   * first op -> ensure the helper is running (ONE launch), then delegate.
//   * launch fails (UAC declined / helper never comes up) -> surface that Fail,
//     do not delegate (no channel).
//   * subsequent ops after a successful launch -> ensure is not called again.
// -----------------------------------------------------------------------
public sealed class LaunchOnDemandElevatedClientTests
{
    private sealed class FakeInner : IElevatedClient
    {
        public int Calls { get; private set; }
        private readonly Result _result;
        public FakeInner(Result? result = null) => _result = result ?? Result.Ok();
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private static PrivilegedOp Op() => new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance);

    [Fact]
    public async Task First_op_ensures_running_then_delegates()
    {
        var ensureCalls = 0;
        var inner = new FakeInner(Result.Ok());
        var sut = new LaunchOnDemandElevatedClient(
            ensureRunningAsync: _ => { ensureCalls++; return Task.FromResult(Result.Ok()); },
            inner: inner);

        var r = await sut.ExecuteAsync(Op(), default);

        r.IsOk.Should().BeTrue();
        ensureCalls.Should().Be(1);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Launch_failure_surfaces_and_does_not_delegate()
    {
        var inner = new FakeInner(Result.Ok());
        var sut = new LaunchOnDemandElevatedClient(
            ensureRunningAsync: _ => Task.FromResult(
                Result.Fail(HelperLauncher.ElevationDeclined, "user cancelled UAC")),
            inner: inner);

        var r = await sut.ExecuteAsync(Op(), default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(HelperLauncher.ElevationDeclined);
        inner.Calls.Should().Be(0, "no channel when the launch failed");
    }

    [Fact]
    public async Task Ensure_running_runs_once_across_multiple_ops()
    {
        var ensureCalls = 0;
        var inner = new FakeInner(Result.Ok());
        var sut = new LaunchOnDemandElevatedClient(
            ensureRunningAsync: _ => { ensureCalls++; return Task.FromResult(Result.Ok()); },
            inner: inner);

        await sut.ExecuteAsync(Op(), default);
        await sut.ExecuteAsync(Op(), default);
        await sut.ExecuteAsync(Op(), default);

        ensureCalls.Should().Be(1, "the helper is launched at most once per client lifetime");
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_failed_launch_is_retried_on_the_next_op()
    {
        // If the first ensure fails (e.g. user dismissed UAC), a later op should be
        // allowed to try again rather than be permanently broken.
        var attempts = 0;
        var inner = new FakeInner(Result.Ok());
        var sut = new LaunchOnDemandElevatedClient(
            ensureRunningAsync: _ =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? Result.Fail(HelperLauncher.ElevationDeclined, "declined")
                    : Result.Ok());
            },
            inner: inner);

        (await sut.ExecuteAsync(Op(), default)).IsOk.Should().BeFalse();
        (await sut.ExecuteAsync(Op(), default)).IsOk.Should().BeTrue();

        attempts.Should().Be(2, "a declined launch must be retryable");
        inner.Calls.Should().Be(1);
    }

    // Revue n° 14 (08/10/2026) : le helper s'arrête seul après 15 min d'inactivité (SEC1). Le client gardait « déjà lancé »
    // et ne le relançait plus jamais : « Annuler » ou « Rendre » échouaient jusqu'au redémarrage de l'App.
    private sealed class HelperQuiSArrete : IElevatedClient
    {
        public bool Vivant = true;
        public int Calls;
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Vivant ? Result.Ok() : Result.Fail(ErrorCodes.HelperUnreachable, "tube absent"));
        }
    }

    [Fact]
    public async Task Un_helper_arrete_est_relance_une_fois_puis_l_op_passe()
    {
        var lancements = 0;
        var inner = new HelperQuiSArrete();
        var sut = new LaunchOnDemandElevatedClient(_ => { lancements++; inner.Vivant = true; return Task.FromResult(Result.Ok()); }, inner);

        (await sut.ExecuteAsync(Op(), default)).IsOk.Should().BeTrue();
        inner.Vivant = false; // 15 minutes plus tard, le helper s'est arrêté

        (await sut.ExecuteAsync(Op(), default)).IsOk.Should().BeTrue("le client relance le helper au lieu d'échouer");
        lancements.Should().Be(2);
    }

    [Fact]
    public async Task Un_helper_qui_ne_revient_pas_n_est_relance_qu_une_fois_par_op()
    {
        var lancements = 0;
        var inner = new HelperQuiSArrete { Vivant = false };
        var sut = new LaunchOnDemandElevatedClient(_ => { lancements++; return Task.FromResult(Result.Ok()); }, inner);

        var r = await sut.ExecuteAsync(Op(), default);

        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperUnreachable);
        lancements.Should().Be(2, "le premier lancement, puis une seule relance");
    }
}
