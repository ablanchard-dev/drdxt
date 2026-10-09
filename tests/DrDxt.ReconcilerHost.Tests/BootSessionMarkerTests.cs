using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// Boot-outcome scheduling (dead-man pattern).
//
// BootSessionMarker:
//   * MarkBootOnceAsync calls IBootOutcomeStore.MarkBootStartedAsync exactly ONCE per
//     process (guarded) — boot is an unconditional +1 to the unclean streak.
//   * MarkStableAfterAsync(window, ct) waits the window via an injected IDelay seam
//     (not a real Task.Delay) then calls MarkSessionStableAsync — the dead-man reset.
//     Cancelled before the window elapses → not marked stable (a boot that crashes
//     before the stable window stays counted, preserving the boot-loop guarantee).
// -----------------------------------------------------------------------
public sealed class BootSessionMarkerTests
{
    private static BootSessionMarker Make(FakeBootOutcomeStore store, IDelay delay)
        => new(store, delay);

    [Fact]
    public async Task MarkBootOnce_IncrementsStreak_OnlyOncePerProcess()
    {
        var store = new FakeBootOutcomeStore();
        var marker = Make(store, new ManualDelay());

        (await marker.MarkBootOnceAsync(default)).IsOk.Should().BeTrue();
        (await marker.MarkBootOnceAsync(default)).IsOk.Should().BeTrue();
        (await marker.MarkBootOnceAsync(default)).IsOk.Should().BeTrue();

        store.MarkBootStartedCount.Should().Be(1, "boot is marked exactly once per process");
        store.Streak.Should().Be(1);
    }

    [Fact]
    public async Task MarkBootOnce_WhenStoreFails_ReturnsFail_AndDoesNotConsumeTheGuard()
    {
        var store = new FakeBootOutcomeStore { Fail = true };
        var marker = Make(store, new ManualDelay());

        var failed = await marker.MarkBootOnceAsync(default);
        failed.IsOk.Should().BeFalse();

        // A failed mark must not "use up" the once-guard: a later retry can still mark.
        store.Fail = false;
        (await marker.MarkBootOnceAsync(default)).IsOk.Should().BeTrue();
        store.MarkBootStartedCount.Should().Be(1);
    }

    [Fact]
    public async Task MarkStableAfter_MarksStable_OnlyAfterTheWindowElapses()
    {
        var store = new FakeBootOutcomeStore { Streak = 3 };
        var delay = new ManualDelay();
        var marker = Make(store, delay);

        var task = marker.MarkStableAfterAsync(TimeSpan.FromMinutes(5), default);

        // Before the window elapses: Not yet marked stable.
        await delay.WaitUntilAwaited();
        store.MarkSessionStableCount.Should().Be(0, "must not mark stable before the window elapses");

        // Elapse the window → stable is marked, streak reset.
        delay.Complete();
        (await task).IsOk.Should().BeTrue();
        store.MarkSessionStableCount.Should().Be(1);
        store.Streak.Should().Be(0);

        delay.LastRequested.Should().Be(TimeSpan.FromMinutes(5), "the marker uses the injected delay, not a real timer");
    }

    [Fact]
    public async Task MarkStableAfter_WhenCancelledBeforeWindow_DoesNotMarkStable()
    {
        var store = new FakeBootOutcomeStore { Streak = 3 };
        var delay = new ManualDelay();
        var cts = new CancellationTokenSource();
        var marker = Make(store, delay);

        var task = marker.MarkStableAfterAsync(TimeSpan.FromMinutes(5), cts.Token);
        await delay.WaitUntilAwaited();

        // Cancel before the window completes (a crash/shutdown mid-window).
        cts.Cancel();
        delay.Cancel();

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();

        store.MarkSessionStableCount.Should().Be(0, "a boot that did not survive the window stays counted");
        store.Streak.Should().Be(3);
    }

    [Fact]
    public async Task MarkStableAfter_WhenStoreFails_ReturnsFail()
    {
        var store = new FakeBootOutcomeStore { Fail = true };
        var delay = new ManualDelay { AutoComplete = true };
        var marker = Make(store, delay);

        var result = await marker.MarkStableAfterAsync(TimeSpan.FromMinutes(5), default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
    }
}

/// <summary>A controllable IDelay. Records the requested duration; completes only when
/// the test says so (or immediately if <see cref="AutoComplete"/>). No real time.</summary>
public sealed class ManualDelay : IDelay
{
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _awaited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool AutoComplete { get; set; }
    public TimeSpan? LastRequested { get; private set; }

    public Task DelayAsync(TimeSpan duration, CancellationToken ct)
    {
        LastRequested = duration;
        _awaited.TrySetResult();

        if (AutoComplete)
            return Task.CompletedTask;

        // Honor cancellation through the same token the production delay would observe.
        ct.Register(() => _tcs.TrySetCanceled(ct));
        return _tcs.Task;
    }

    public Task WaitUntilAwaited() => _awaited.Task;
    public void Complete() => _tcs.TrySetResult();
    public void Cancel() => _tcs.TrySetCanceled();
}
