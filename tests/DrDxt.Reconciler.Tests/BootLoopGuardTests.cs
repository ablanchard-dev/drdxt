using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// T27b — the anti boot-loop guard threshold logic, in isolation.
// -----------------------------------------------------------------------
public sealed class BootLoopGuardTests
{
    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]   // at threshold → suspend
    [InlineData(5, 3, true)]   // over threshold → suspend
    public async Task ShouldSuspend_IsTrue_AtOrOverThreshold(int streak, int threshold, bool expected)
    {
        var store = new FakeBootOutcomeStore { Streak = streak };
        var guard = new BootLoopGuard(store, threshold);

        (await guard.ShouldSuspendGpuReapplyAsync(default)).Should().Be(expected);
    }

    [Fact]
    public async Task ShouldSuspend_FailsSafe_WhenStreakUnreadable()
    {
        // If we cannot confirm boots are healthy, suspend GPU re-apply (do not
        // re-impose a tweak that might be the cause of the loop) — fail-safe.
        var store = new FakeBootOutcomeStore { Fail = true };
        var guard = new BootLoopGuard(store, threshold: 3);

        (await guard.ShouldSuspendGpuReapplyAsync(default)).Should().BeTrue();
    }
}
