using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// La logique de seuil de la garde anti-boucle de démarrage, isolée.
// -----------------------------------------------------------------------
public sealed class BootLoopGuardTests
{
    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]   // au seuil → suspendre
    [InlineData(5, 3, true)]   // au-dessus du seuil → suspendre
    public async Task ShouldSuspend_IsTrue_AtOrOverThreshold(int streak, int threshold, bool expected)
    {
        var store = new FakeBootOutcomeStore { Streak = streak };
        var guard = new BootLoopGuard(store, threshold);

        (await guard.ShouldSuspendGpuReapplyAsync(default)).Should().Be(expected);
    }

    [Fact]
    public async Task ShouldSuspend_FailsSafe_WhenStreakUnreadable()
    {
        // Si on ne peut pas confirmer que les démarrages sont sains, suspendre la réapplication GPU
        // (ne pas réimposer un levier qui pourrait causer la boucle) : sûreté intégrée.
        var store = new FakeBootOutcomeStore { Fail = true };
        var guard = new BootLoopGuard(store, threshold: 3);

        (await guard.ShouldSuspendGpuReapplyAsync(default)).Should().BeTrue();
    }
}
