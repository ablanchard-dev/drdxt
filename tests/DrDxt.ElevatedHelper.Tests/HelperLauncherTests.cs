using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// HelperLauncher decision logic — unit-tested behind a process-launch seam and a
// connect+handshake ATTEMPT seam (NO separate bare-connect liveness probe; FIX 1).
//
// The real `runas` (UseShellExecute + Verb="runas") spawn is the untested live
// edge (documented in HelperLauncher). These tests cover the branching:
//   * already running (a handshake attempt succeeds first try)  -> REUSE, no launch
//   * not running (connect fails)                               -> launch
//   * launched then comes up within the deadline                -> Ok
//   * launched but never comes up before the deadline           -> HELPER_UNREACHABLE
//   * a live pipe that REJECTS the handshake (auth/protocol)     -> fail, no launch
//   * UAC declined                                              -> ELEVATION_DECLINED
//
// There is exactly ONE accept per attempt: the attempt seam does a COMPLETE
// connect+handshake (no truncated, probe-consumed slot, no pipe re-creation race).
// -----------------------------------------------------------------------
public sealed class HelperLauncherTests
{
    // Records launch attempts and lets a test script the outcome. Optionally flips
    // the attempt seam to "connectable" on launch (the helper comes up after spawn).
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

    // A connect+handshake attempt whose outcome the test controls. It can flip from
    // "connect fails" to a launched-state outcome after a launch, simulating the
    // helper's pipe coming up.
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

    // --- not running (connect fails) -> launch happens, comes up -> Ok ---
    [Fact]
    public async Task Launches_when_helper_not_running()
    {
        var attempt = new FakeAttempt(
            HelperAttemptOutcome.ConnectFailed, afterLaunch: HelperAttemptOutcome.Connected);
        var launcher = new FakeProcessLauncher(markAlive: attempt); // helper comes up on launch
        var sut = new HelperLauncher(launcher, attempt);

        var r = await sut.EnsureRunningAsync(Spec(), default, postLaunchWaitMs: 500, pollIntervalMs: 10);

        r.IsOk.Should().BeTrue();
        launcher.LaunchCount.Should().Be(1);
        launcher.LastSpec.Should().Be(Spec());
    }

    // --- already running (handshake succeeds first try) -> reuse, NO launch ---
    [Fact]
    public async Task Reuses_when_helper_already_running()
    {
        var launcher = new FakeProcessLauncher();
        var attempt = new FakeAttempt(HelperAttemptOutcome.Connected);
        var sut = new HelperLauncher(launcher, attempt);

        var r = await sut.EnsureRunningAsync(Spec(), default);

        r.IsOk.Should().BeTrue();
        launcher.LaunchCount.Should().Be(0); // reused, never spawned
        attempt.Calls.Should().Be(1, "reuse is detected by ONE successful handshake, not a bare probe");
    }

    // --- UAC declined -> clean Fail(ELEVATION_DECLINED), no throw ---
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

    // --- launch succeeds but the helper never comes up -> HELPER_UNREACHABLE ---
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

    // --- a live pipe that REJECTS the handshake (auth/protocol) -> fail, NO launch.
    //     A hostile/mismatched helper is already up; we must not spawn a 2nd elevated
    //     process onto it, and we must not spin retrying an auth denial. ---
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

    // --- launch ok and the helper comes up shortly after -> Ok ---
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

    // --- a null/empty helper path is rejected fail-clean (no spawn) ---
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

    // --- a post-launch handshake REJECTION fails immediately (no spin to deadline) ---
    [Fact]
    public async Task Post_launch_handshake_rejection_fails_fast()
    {
        var launcher = new FakeProcessLauncher(Result.Ok());
        // Connect fails first (so we launch), then a server comes up that REJECTS us.
        var attempt = new FakeAttempt(
            HelperAttemptOutcome.ConnectFailed, afterLaunch: HelperAttemptOutcome.HandshakeRejected);
        var launcherThatMarks = new MarkingLauncher2(attempt, launcher);
        var sut = new HelperLauncher(launcherThatMarks, attempt);

        var r = await sut.EnsureRunningAsync(Spec(), default, postLaunchWaitMs: 5000, pollIntervalMs: 10);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    // Marks the attempt as "launched" AND forwards the launch to a real fake launcher
    // so LaunchCount is tracked.
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
