using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Logique de décision de HelperLauncher, testée derrière une couture de lancement de
// processus et une couture de tentative connexion + négociation (pas de sonde de vie séparée).
//
// Le vrai lancement `runas` (UseShellExecute + Verb="runas") est le bord vivant non testé
// (documenté dans HelperLauncher). Ces tests couvrent les branches :
//   * déjà lancé (une tentative de négociation réussit du premier coup) → réutiliser, pas de lancement
//   * pas lancé (la connexion échoue)                                  → lancer
//   * lancé puis joignable avant l'échéance                            → Ok
//   * lancé mais jamais joignable avant l'échéance                     → HELPER_UNREACHABLE
//   * un tube vivant qui refuse la négociation (authentification/protocole) → échec, pas de lancement
//   * UAC refusé                                                        → ELEVATION_DECLINED
//
// Il y a exactement un accept par tentative : la couture fait une connexion + négociation
// complète (pas de créneau consommé par une sonde, pas de course à la recréation du tube).
// -----------------------------------------------------------------------
public sealed class HelperLauncherTests
{
    // Enregistre les tentatives de lancement et laisse un test en scénariser l'issue. Peut
    // rendre la couture de tentative « connectable » au lancement (le helper monte après le spawn).
    private sealed class FakeProcessLauncher : IHelperProcessLauncher
    {
        public int LaunchCount { get; private set; }
        private readonly Result _result;
        private readonly FakeAttempt? _markAlive;
        public FakeProcessLauncher(Result? result = null, FakeAttempt? markAlive = null)
        {
            _result = result ?? Result.Ok();
            _markAlive = markAlive;
        }
        public Result Launch(HelperLaunchSpec spec)
        {
            LaunchCount++;
            LastSpec = spec;
            if (_result.IsOk)
                _markAlive?.MarkLaunched();
            return _result;
        }
        public HelperLaunchSpec? LastSpec { get; private set; }
    }

    // Tentative connexion + négociation dont le test contrôle l'issue. Elle peut passer de
    // « la connexion échoue » à l'issue d'après lancement, pour simuler le tube du helper
    // qui monte.
    private sealed class FakeAttempt : IHelperHandshakeAttempt
    {
        private HelperAttemptOutcome _current;
        private readonly HelperAttemptOutcome _afterLaunch;
        public FakeAttempt(
            HelperAttemptOutcome initial,
            HelperAttemptOutcome afterLaunch = HelperAttemptOutcome.Connected)
        {
            _current = initial;
            _afterLaunch = afterLaunch;
        }
        public int Calls { get; private set; }
        public void MarkLaunched() => _current = _afterLaunch;
        public Task<HelperAttemptOutcome> TryHandshakeAsync(HelperLaunchSpec spec, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_current);
        }
    }

    private static HelperLaunchSpec Spec() =>
        new(@"C:\Program Files\DrDXT\DrDxt.ElevatedHelper.exe",
            @"C:\Program Files\DrDXT\DrDxt.App.exe",
            "DrDXT.Elevated");

    // --- pas lancé (la connexion échoue) → le lancement a lieu, le helper monte → Ok ---
    [Fact]
    public async Task Launches_when_helper_not_running()
    {
        var attempt = new FakeAttempt(
            HelperAttemptOutcome.ConnectFailed, afterLaunch: HelperAttemptOutcome.Connected);
        var launcher = new FakeProcessLauncher(markAlive: attempt); // le helper monte au lancement
        var sut = new HelperLauncher(launcher, attempt);

        var r = await sut.EnsureRunningAsync(Spec(), default, postLaunchWaitMs: 500, pollIntervalMs: 10);

        r.IsOk.Should().BeTrue();
        launcher.LaunchCount.Should().Be(1);
        launcher.LastSpec.Should().Be(Spec());
    }

    // --- déjà lancé (la négociation réussit du premier coup) → réutiliser, pas de lancement ---
    [Fact]
    public async Task Reuses_when_helper_already_running()
    {
        var launcher = new FakeProcessLauncher();
        var attempt = new FakeAttempt(HelperAttemptOutcome.Connected);
        var sut = new HelperLauncher(launcher, attempt);

        var r = await sut.EnsureRunningAsync(Spec(), default);

        r.IsOk.Should().BeTrue();
        launcher.LaunchCount.Should().Be(0); // réutilisé, jamais lancé
        attempt.Calls.Should().Be(1, "reuse is detected by ONE successful handshake, not a bare probe");
    }

    // --- UAC refusé → Fail(ELEVATION_DECLINED) propre, pas d'exception ---
    [Fact]
    public async Task Fails_clean_when_uac_declined()
    {
        var attempt = new FakeAttempt(HelperAttemptOutcome.ConnectFailed);
        var launcher = new FakeProcessLauncher(
            Result.Fail(HelperLauncher.ElevationDeclined, "user cancelled UAC"));
        var sut = new HelperLauncher(launcher, attempt);

        var r = await sut.EnsureRunningAsync(Spec(), default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(HelperLauncher.ElevationDeclined);
        launcher.LaunchCount.Should().Be(1);
    }

    // --- le lancement réussit mais le helper ne monte jamais → HELPER_UNREACHABLE ---
    [Fact]
    public async Task Fails_unreachable_when_launch_ok_but_helper_never_comes_up()
    {
        var launcher = new FakeProcessLauncher(Result.Ok());
        var attempt = new FakeAttempt(
            HelperAttemptOutcome.ConnectFailed, afterLaunch: HelperAttemptOutcome.ConnectFailed);
        var sut = new HelperLauncher(launcher, attempt);

        var r = await sut.EnsureRunningAsync(Spec(), default, postLaunchWaitMs: 50, pollIntervalMs: 10);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperUnreachable);
    }

    // --- un tube vivant qui refuse la négociation (authentification/protocole) → échec, pas de lancement.
    //     Un helper hostile ou dépareillé tourne déjà ; on ne lance pas un second processus
    //     élevé par-dessus, et on ne boucle pas sur un refus d'authentification. ---
    [Fact]
    public async Task Live_pipe_that_rejects_handshake_fails_without_launching()
    {
        var launcher = new FakeProcessLauncher();
        var attempt = new FakeAttempt(HelperAttemptOutcome.HandshakeRejected);
        var sut = new HelperLauncher(launcher, attempt);

        var r = await sut.EnsureRunningAsync(Spec(), default);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
        launcher.LaunchCount.Should().Be(0, "a live but rejecting pipe must not trigger another elevation");
    }

    // --- lancement ok et le helper monte peu après → Ok ---
    [Fact]
    public async Task Ok_when_helper_becomes_live_after_launch()
    {
        var attempt = new FakeAttempt(
            HelperAttemptOutcome.ConnectFailed, afterLaunch: HelperAttemptOutcome.Connected);
        var launcherThatMarks = new MarkingLauncher(attempt);
        var sut = new HelperLauncher(launcherThatMarks, attempt);

        var r = await sut.EnsureRunningAsync(Spec(), default, postLaunchWaitMs: 200, pollIntervalMs: 10);

        r.IsOk.Should().BeTrue();
    }

    private sealed class MarkingLauncher : IHelperProcessLauncher
    {
        private readonly FakeAttempt _attempt;
        public MarkingLauncher(FakeAttempt attempt) => _attempt = attempt;
        public Result Launch(HelperLaunchSpec spec)
        {
            _attempt.MarkLaunched();
            return Result.Ok();
        }
    }

    // --- un chemin de helper nul ou vide est refusé proprement (pas de spawn) ---
    [Fact]
    public async Task Rejects_empty_helper_path()
    {
        var attempt = new FakeAttempt(HelperAttemptOutcome.ConnectFailed);
        var launcher = new FakeProcessLauncher();
        var sut = new HelperLauncher(launcher, attempt);

        var r = await sut.EnsureRunningAsync(
            new HelperLaunchSpec("", @"C:\app.exe", "pipe"), default);

        r.IsOk.Should().BeFalse();
        launcher.LaunchCount.Should().Be(0);
    }

    // --- un refus de négociation après lancement échoue aussitôt (pas de boucle jusqu'à l'échéance) ---
    [Fact]
    public async Task Post_launch_handshake_rejection_fails_fast()
    {
        var launcher = new FakeProcessLauncher(Result.Ok());
        // La connexion échoue d'abord (donc on lance), puis un serveur monte et nous refuse.
        var attempt = new FakeAttempt(
            HelperAttemptOutcome.ConnectFailed, afterLaunch: HelperAttemptOutcome.HandshakeRejected);
        var launcherThatMarks = new MarkingLauncher2(attempt, launcher);
        var sut = new HelperLauncher(launcherThatMarks, attempt);

        var r = await sut.EnsureRunningAsync(Spec(), default, postLaunchWaitMs: 5000, pollIntervalMs: 10);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    // Marque la tentative « lancée » et transmet le lancement au faux lanceur réel,
    // pour que LaunchCount soit suivi.
    private sealed class MarkingLauncher2 : IHelperProcessLauncher
    {
        private readonly FakeAttempt _attempt;
        private readonly FakeProcessLauncher _inner;
        public MarkingLauncher2(FakeAttempt attempt, FakeProcessLauncher inner)
        {
            _attempt = attempt;
            _inner = inner;
        }
        public Result Launch(HelperLaunchSpec spec)
        {
            var r = _inner.Launch(spec);
            _attempt.MarkLaunched();
            return r;
        }
    }
}
