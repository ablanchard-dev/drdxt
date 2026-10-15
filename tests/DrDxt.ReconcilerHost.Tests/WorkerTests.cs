using DrDxt.Core;
using DrDxt.Reconciler;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// Sémantique du Worker : l'hôte doit réconcilier inconditionnellement au démarrage et à chaque
// scrutation quotidienne. La détection de mise à jour de fonctionnalité est un déclencheur et
// un enregistreur en plus, jamais une porte.
// Toute la raison d'être du réconciliateur est la persistance après redémarrage : un décalage
// d'horloge GPU volatil (perdu à chaque redémarrage) et tout réglage de registre ou d'alimentation
// dérivé doivent être réappliqués à chaque démarrage, que le build de l'OS ait changé ou non. Le
// passage est idempotent (il ne réapplique que les éléments dérivés ; une machine propre = rien,
// zéro écriture), donc le jouer à chaque déclencheur est sûr et correct.
//
// Ces tests utilisent les coutures existantes de l'hôte : un tic à vol unique instrumenté pour
// compter les passages, un IDelay pilotable (pas de vrai minuteur), et les faux en mémoire des
// unités du déclencheur de mise à jour. Ils figent aussi les invariants préservés :
//   * vol unique : les passages passent par SingleFlightReconcile (sérialisés).
//   * marquage homme mort du démarrage : MarkBootOnce exactement une fois au démarrage ; MarkStable
//     après la fenêtre ; une annulation avant la fenêtre laisse le démarrage compté.
//   * l'effet de bord d'enregistrement : une mise à jour de fonctionnalité détectée enregistre
//     toujours LastFeatureUpdateBuild.
// -----------------------------------------------------------------------
public sealed class WorkerTests
{
    private static readonly TimeSpan StableWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(24);

    // Construit un Worker sur des faux entièrement en mémoire. Le passage de réconciliation est le
    // compteur instrumenté, pour qu'un test vérifie exactement combien de passages ont tourné. Le
    // déclencheur de mise à jour est composé des faux standard sonde/magasin/signal fournis par l'appelant.
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
        // Build inchangé + aucun événement de corroboration = pas de mise à jour de fonctionnalité.
        // Le Worker réconcilie inconditionnellement, sans retour anticipé.
        var probe = new FakeOsBuildProbe { Build = "22631.3447" };
        var stateStore = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var signal = new FakeUpdateSignal { Recent = false };
        var bootStore = new FakeBootOutcomeStore();
        var delay = new BlockingDelay();
        var (worker, tick) = Build(probe, stateStore, signal, bootStore, delay, new RecordingDiagnostics());

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);

        // Attendre que le passage de démarrage atterrisse, puis arrêter proprement.
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
        // Aucune mise à jour à aucun tic ; on veut quand même un passage au démarrage + un par scrutation.
        var probe = new FakeOsBuildProbe { Build = "22631.3447" };
        var stateStore = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var signal = new FakeUpdateSignal { Recent = false };
        var bootStore = new FakeBootOutcomeStore();
        var delay = new BlockingDelay();
        var (worker, tick) = Build(probe, stateStore, signal, bootStore, delay, new RecordingDiagnostics());

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);

        // Passage de démarrage.
        await tick.WaitForPasses(1);

        // Libérer le premier délai de scrutation quotidienne pour qu'un passage quotidien tourne inconditionnellement.
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
        // Build changé au démarrage : une vraie mise à jour de fonctionnalité. Le détecteur (appelé dans
        // la boucle pour son effet de bord) doit enregistrer le nouveau build, et le passage de démarrage doit tourner.
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
        // Même si la lecture du déclencheur de mise à jour échoue (sonde de build illisible), la
        // réconciliation inconditionnelle doit tourner : le déclencheur est additif, jamais une porte.
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
        // Homme mort : annuler avant la fin de la fenêtre stable → la session n'est pas marquée stable.
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
        // Arrêter sans jamais libérer le délai de la fenêtre stable.
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
            // Arrêt normal.
        }
    }

    // Un corps de réconciliation instrumenté (le délégué de SingleFlightReconcile). Compte les passages
    // et signale les attendants pour qu'un test attende « N passages ont tourné » sans vrai minuteur.
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

        // Tourne (brièvement, sans boucle de sommeil réelle sur le chemin chaud) jusqu'à ce qu'au moins
        // <paramref name="count"/> passages aient tourné. Délai généreux pour qu'une régression soit attrapée, pas suspendue.
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

    // Un IDelay pilotable qui distingue l'attente de la fenêtre stable (la première, longue demande
    // StableWindow) des attentes de scrutation quotidienne. Le délai de fenêtre stable ne s'achève
    // jamais seul (un test qui ne le libère pas peut ainsi prouver l'invariant de l'homme mort) ;
    // chaque délai de scrutation bloque jusqu'à ce que le test le libère. L'annulation est
    // honorée par le même jeton que le délai de production observe.
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
                // La fenêtre stable de l'homme mort : la laisser en attente (ne s'achève jamais seule).
                return tcs.Task;
            }

            // Une attente de scrutation quotidienne : l'enregistrer pour qu'un test en libère exactement une.
            lock (_lock)
            {
                _pendingPoll = tcs;
                _pollRequested.TrySetResult();
            }
            return tcs.Task;
        }

        // Attend que la boucle ait demandé son prochain délai de scrutation quotidienne, puis l'achève
        // pour qu'exactement un passage quotidien avance.
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
