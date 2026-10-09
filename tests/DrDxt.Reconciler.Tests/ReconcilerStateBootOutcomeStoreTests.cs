using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// T27b write path — ReconcilerStateBootOutcomeStore maintains the unclean-boot
// streak in the runtime sidecar (dead-man pattern): MarkBootStarted increments,
// MarkSessionStable resets to 0 and stamps the clock, GetUncleanStreak reads.
// Pure (Core only); each op is Load → mutate → Save, fail-clean (no partial write).
// -----------------------------------------------------------------------
public sealed class ReconcilerStateBootOutcomeStoreTests
{
    private static ReconcilerStateBootOutcomeStore Make(
        InMemoryReconcilerStateStore stateStore, FakeClock clock)
        => new(stateStore, clock);

    [Fact]
    public async Task MarkBootStarted_IncrementsStreak_AndPersists()
    {
        var stateStore = new InMemoryReconcilerStateStore();
        var store = Make(stateStore, new FakeClock());

        (await store.MarkBootStartedAsync(default)).IsOk.Should().BeTrue();
        stateStore.Saved!.UncleanBootStreak.Should().Be(1);

        (await store.MarkBootStartedAsync(default)).IsOk.Should().BeTrue();
        stateStore.Saved!.UncleanBootStreak.Should().Be(2);
    }

    [Fact]
    public async Task MarkSessionStable_ResetsStreakToZero_AndStampsLastStableAt()
    {
        var clock = new FakeClock();
        var stateStore = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { UncleanBootStreak = 4 });
        var store = Make(stateStore, clock);

        clock.Advance(TimeSpan.FromMinutes(7));
        var expectedAt = clock.UtcNow;

        (await store.MarkSessionStableAsync(default)).IsOk.Should().BeTrue();

        stateStore.Saved!.UncleanBootStreak.Should().Be(0);
        stateStore.Saved!.LastStableSessionAt.Should().Be(expectedAt);
    }

    [Fact]
    public async Task GetUncleanStreak_ReadsPersistedValue()
    {
        var stateStore = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { UncleanBootStreak = 3 });
        var store = Make(stateStore, new FakeClock());

        var streak = await store.GetUncleanStreakAsync(default);

        streak.IsOk.Should().BeTrue();
        streak.Value.Should().Be(3);
    }

    [Fact]
    public async Task GetUncleanStreak_WhenLoadFails_ReturnsFail()
    {
        var stateStore = new InMemoryReconcilerStateStore { FailLoad = true };
        var store = Make(stateStore, new FakeClock());

        var streak = await store.GetUncleanStreakAsync(default);

        streak.IsOk.Should().BeFalse();
        streak.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
    }

    [Fact]
    public async Task MarkBootStarted_WhenLoadFails_ReturnsFail_NoSave()
    {
        var stateStore = new InMemoryReconcilerStateStore { FailLoad = true };
        var store = Make(stateStore, new FakeClock());

        var result = await store.MarkBootStartedAsync(default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
        stateStore.SaveCount.Should().Be(0, "a failed Load must never reach Save");
    }

    [Fact]
    public async Task MarkBootStarted_WhenSaveFails_ReturnsFail()
    {
        var stateStore = new InMemoryReconcilerStateStore { FailSave = true };
        var store = Make(stateStore, new FakeClock());

        var result = await store.MarkBootStartedAsync(default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
    }

    [Fact]
    public async Task MarkSessionStable_WhenLoadFails_ReturnsFail_NoSave()
    {
        var stateStore = new InMemoryReconcilerStateStore { FailLoad = true };
        var store = Make(stateStore, new FakeClock());

        var result = await store.MarkSessionStableAsync(default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
        stateStore.SaveCount.Should().Be(0, "a failed Load must never reach Save");
    }

    [Fact]
    public async Task MarkSessionStable_WhenSaveFails_ReturnsFail()
    {
        var stateStore = new InMemoryReconcilerStateStore { FailSave = true };
        var store = Make(stateStore, new FakeClock());

        var result = await store.MarkSessionStableAsync(default);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.IoFailure);
    }

    [Fact]
    public async Task RoundTrip_WithRealJsonStore_MarkBootTwiceThenStable()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "DrDxtBoot_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var jsonStore = new JsonReconcilerStateStore(path);
            var store = new ReconcilerStateBootOutcomeStore(jsonStore, new FakeClock());

            (await store.MarkBootStartedAsync(default)).IsOk.Should().BeTrue();
            (await store.MarkBootStartedAsync(default)).IsOk.Should().BeTrue();

            var afterTwoBoots = await store.GetUncleanStreakAsync(default);
            afterTwoBoots.IsOk.Should().BeTrue();
            afterTwoBoots.Value.Should().Be(2);

            (await store.MarkSessionStableAsync(default)).IsOk.Should().BeTrue();

            var afterStable = await store.GetUncleanStreakAsync(default);
            afterStable.IsOk.Should().BeTrue();
            afterStable.Value.Should().Be(0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
