using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// Planification de l'issue de démarrage (motif de l'homme mort).
//
// BootSessionMarker :
//   * MarkBootOnceAsync appelle IBootOutcomeStore.MarkBootStartedAsync exactement une fois par
//     processus (gardé) : un démarrage est un +1 inconditionnel à la série non propre.
//   * MarkStableAfterAsync(fenêtre, ct) attend la fenêtre via une couture IDelay injectée
//     (pas un vrai Task.Delay) puis appelle MarkSessionStableAsync : la remise à zéro de l'homme mort.
//     Annulé avant la fin de la fenêtre → pas marqué stable (un démarrage qui plante avant
//     la fenêtre stable reste compté, ce qui préserve la garantie anti-boucle).
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

        // Un marquage raté ne doit pas « consommer » la garde unique : un nouvel essai peut encore marquer.
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

        // Avant la fin de la fenêtre : pas encore marqué stable.
        await delay.WaitUntilAwaited();
        store.MarkSessionStableCount.Should().Be(0, "must not mark stable before the window elapses");

        // Écouler la fenêtre → stable est marqué, série remise à zéro.
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

        // Annuler avant la fin de la fenêtre (plantage ou arrêt en pleine fenêtre).
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

/// <summary>IDelay pilotable. Enregistre la durée demandée ; ne s'achève que quand le test
/// le dit (ou aussitôt si <see cref="AutoComplete"/>). Pas de temps réel.</summary>
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

        // Honorer l'annulation par le même jeton que le délai de production observerait.
        ct.Register(() => _tcs.TrySetCanceled(ct));
        return _tcs.Task;
    }

    public Task WaitUntilAwaited() => _awaited.Task;
    public void Complete() => _tcs.TrySetResult();
    public void Cancel() => _tcs.TrySetCanceled();
}
