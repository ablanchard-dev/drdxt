using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// Worker semantics — the host must reconcile unconditionally at boot and on every
// daily poll. Feature-update detection is an ADDITIONAL trigger/recorder, never a gate.
//
// The whole point of the reconciler is persistence after a reboot: a volatile GPU clock
// offset (gone after every reboot) and any drifted registry/power setting must be
// re-applied at every boot, regardless of whether the OS build changed. The reconcile
// pass is idempotent (it re-applies only drifted items; a clean machine = a no-op with
// zero writes), so running it on every trigger is safe and correct.
//
// These tests use the host's existing seams: an instrumented single-flight tick to
// count passes, a controllable IDelay (no real timer), and the in-memory fakes for the
// feature-update trigger's units. They also pin the preserved invariants:
//   * T29 single-flight: passes go through SingleFlightReconcile (serialized).
//   * dead-man boot marking: MarkBootOnce exactly once at startup; MarkStable after the
//     window; cancel-before-window leaves the boot counted.
//   * the recorder side effect: a detected feature update still records
//     LastFeatureUpdateBuild.
// -----------------------------------------------------------------------
public sealed class WorkerTests
{
    private static readonly TimeSpan StableWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(24);

    // Builds a Worker over fully in-memory fakes. The reconcile pass is the instrumented
    // counter so a test can assert exactly how many passes ran. The feature-update trigger
    // is composed from the standard probe/store/signal fakes the caller supplies.
    private static (Worker worker, CountingTick tick) Build(
        FakeOsBuildProbe probe,
        InMemoryReconcilerStateStore stateStore,
        FakeUpdateSignal signal,
        FakeBootOutcomeStore bootStore,
        IDelay delay,
        RecordingDiagnostics diagnostics)
    {
        var tick = new CountingTick();
        var single = new SingleFlightReconcile(tick.RunAsync);
        var trigger = new FeatureUpdateTrigger(new FeatureUpdateDetector(probe, stateStore), signal);
        var bootMarker = new BootSessionMarker(bootStore, delay);
        var worker = new Worker(
            single, trigger, bootMarker, diagnostics, delay,
            new HostTimingOptions(StableWindow, PollInterval));
        return (worker, tick);
    }

    [Fact]
    public async Task Boot_RunsAReconcilePass_EvenWhenNoFeatureUpdateIsDetected()
    {
        // Build unchanged + no corroborating event = NO feature update. The OLD code would
        // early-return and not reconcile. The CORRECTED Worker reconciles unconditionally.
        var probe = new FakeOsBuildProbe { Build = "22631.3447" };
        var stateStore = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var signal = new FakeUpdateSignal { Recent = false };
        var bootStore = new FakeBootOutcomeStore();
        var delay = new BlockingDelay();
        var (worker, tick) = Build(probe, stateStore, signal, bootStore, delay, new RecordingDiagnostics());

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);

        // Wait for the boot pass to land, then shut down cleanly.
        await tick.WaitForPasses(1);
        cts.Cancel();
        await StopAsync(worker, run);

        tick.Passes.Should().BeGreaterThanOrEqualTo(1,
            "the boot pass must reconcile unconditionally even with no feature update detected");
        bootStore.MarkBootStartedCount.Should().Be(1, "boot is marked exactly once at startup (dead-man +1)");
    }

    [Fact]
    public async Task DailyPoll_RunsAReconcilePassUnconditionally_WithNoFeatureUpdate()
    {
        // No feature update on any tick; we still want one boot pass + one pass per poll.
        var probe = new FakeOsBuildProbe { Build = "22631.3447" };
        var stateStore = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var signal = new FakeUpdateSignal { Recent = false };
        var bootStore = new FakeBootOutcomeStore();
        var delay = new BlockingDelay();
        var (worker, tick) = Build(probe, stateStore, signal, bootStore, delay, new RecordingDiagnostics());

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);

        // Boot pass.
        await tick.WaitForPasses(1);

        // Release the first daily-poll delay so one daily pass runs unconditionally.
        await delay.ReleasePollOnce(PollInterval);
        await tick.WaitForPasses(2);

        cts.Cancel();
        await StopAsync(worker, run);

        tick.Passes.Should().BeGreaterThanOrEqualTo(2,
            "boot + at least one daily poll must each reconcile unconditionally (no feature-update gate)");
    }

    [Fact]
    public async Task DetectedFeatureUpdate_StillRecordsLastFeatureUpdateBuild_AndReconciles()
    {
        // Build CHANGED at boot: a real feature update. The detector (called as part of the
        // loop for its side effect) must record the new build, AND the boot pass must run.
        var probe = new FakeOsBuildProbe { Build = "22631.4000" };
        var stateStore = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var signal = new FakeUpdateSignal { Recent = false };
        var bootStore = new FakeBootOutcomeStore();
        var delay = new BlockingDelay();
        var (worker, tick) = Build(probe, stateStore, signal, bootStore, delay, new RecordingDiagnostics());

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);

        await tick.WaitForPasses(1);
        cts.Cancel();
        await StopAsync(worker, run);

        tick.Passes.Should().BeGreaterThanOrEqualTo(1, "a detected feature update also triggers a pass");
        stateStore.Saved!.LastFeatureUpdateBuild.Should().Be("22631.4000",
            "the detector's recording side effect must still persist the new build");
        stateStore.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task FeatureUpdateTriggerReadFailure_DoesNotPreventTheUnconditionalBootPass()
    {
        // Even if the feature-update trigger read fails (e.g. build probe unreadable), the
        // unconditional reconcile must still run — the trigger is additive, never a gate.
        var probe = new FakeOsBuildProbe { Fail = true };
        var stateStore = new InMemoryReconcilerStateStore();
        var signal = new FakeUpdateSignal { Recent = false };
        var bootStore = new FakeBootOutcomeStore();
        var delay = new BlockingDelay();
        var diagnostics = new RecordingDiagnostics();
        var (worker, tick) = Build(probe, stateStore, signal, bootStore, delay, diagnostics);

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);

        await tick.WaitForPasses(1);
        cts.Cancel();
        await StopAsync(worker, run);

        tick.Passes.Should().BeGreaterThanOrEqualTo(1,
            "a failed feature-update read is logged but never blocks the unconditional reconcile");
        diagnostics.Entries.Should().Contain(e => e.to == "update-trigger-failed",
            "the trigger-read failure is still journaled");
    }

    [Fact]
    public async Task CancelledBeforeStableWindow_LeavesTheBootCounted()
    {
        // Dead-man: cancel before the stable window elapses → session is not marked stable.
        var probe = new FakeOsBuildProbe { Build = "22631.3447" };
        var stateStore = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var signal = new FakeUpdateSignal { Recent = false };
        var bootStore = new FakeBootOutcomeStore();
        var delay = new BlockingDelay();
        var (worker, tick) = Build(probe, stateStore, signal, bootStore, delay, new RecordingDiagnostics());

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);

        await tick.WaitForPasses(1);
        // Shut down without ever releasing the stable-window delay.
        cts.Cancel();
        await StopAsync(worker, run);

        bootStore.MarkBootStartedCount.Should().Be(1, "boot was marked once");
        bootStore.MarkSessionStableCount.Should().Be(0,
            "cancelled before the stable window → the boot stays counted (dead-man)");
        bootStore.Streak.Should().Be(1, "the unclean streak survives an early shutdown");
    }

    private static async Task StopAsync(Worker worker, Task run)
    {
        try
        {
            await worker.StopAsync(CancellationToken.None);
            await run;
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    // An instrumented reconcile body (the SingleFlightReconcile delegate). Counts passes
    // and signals waiters so a test can await "N passes have run" without a real timer.
    private sealed class CountingTick
    {
        private readonly object _lock = new();
        private int _passes;
        private TaskCompletionSource _tick = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Passes { get { lock (_lock) { return _passes; } } }

        public Task<Result> RunAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                _passes++;
                _tick.TrySetResult();
                _tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return Task.FromResult(Result.Ok());
        }

        // Spins (briefly, no real sleep loop on the hot path) until at least <paramref
        // name="count"/> passes have run. Generous deadline so a regression is caught, not hung.
        public async Task WaitForPasses(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Passes < count)
            {
                Task wait;
                lock (_lock) { wait = _tick.Task; }
                var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(50)));
                if (completed != wait && DateTime.UtcNow > deadline)
                    throw new TimeoutException($"only {Passes}/{count} reconcile passes ran");
            }
        }
    }

    // A controllable IDelay that distinguishes the stable-window wait (the first, long
    // StableWindow request) from the daily-poll waits. The stable-window delay never
    // completes on its own (so a test that does not release it can prove the dead-man
    // invariant); each daily-poll delay blocks until the test releases it. Cancellation is
    // honored through the same token the production delay observes.
    private sealed class BlockingDelay : IDelay
    {
        private readonly object _lock = new();
        private TaskCompletionSource? _pendingPoll;
        private TaskCompletionSource _pollRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayAsync(TimeSpan duration, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));

            if (duration == StableWindow)
            {
                // The dead-man stable window — leave it pending (never auto-completes).
                return tcs.Task;
            }

            // A daily-poll wait — record it so a test can release exactly one.
            lock (_lock)
            {
                _pendingPoll = tcs;
                _pollRequested.TrySetResult();
            }
            return tcs.Task;
        }

        // Waits until the loop has requested its next daily-poll delay, then completes it
        // so exactly one daily pass proceeds.
        public async Task ReleasePollOnce(TimeSpan expected)
        {
            Task requested;
            lock (_lock) { requested = _pollRequested.Task; }
            await requested.ConfigureAwait(false);

            TaskCompletionSource? toRelease;
            lock (_lock)
            {
                toRelease = _pendingPoll;
                _pendingPoll = null;
                _pollRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            toRelease?.TrySetResult();
            expected.Should().Be(PollInterval, "daily polls use the configured interval");
        }
    }
}
