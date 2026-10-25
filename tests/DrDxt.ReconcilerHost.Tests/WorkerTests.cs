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
        RecordingDiagnostics diagnostics,
        Func<bool>? jeuEnCours = null,
        FinDuRejeu? finDuRejeu = null)
    {
        var tick = new CountingTick();
        var single = new SingleFlightReconcile(tick.RunAsync);
        var trigger = new FeatureUpdateTrigger(new FeatureUpdateDetector(probe, stateStore), signal);
        var bootMarker = new BootSessionMarker(bootStore, delay);
        var worker = new Worker(
            single, trigger, bootMarker, diagnostics, delay,
            new HostTimingOptions(StableWindow, PollInterval), jeuEnCours ?? (() => false), finDuRejeu);
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

    /// <summary>Une partie que le test termine quand il veut.</summary>
    private sealed class PartieEnCours : IDelay
    {
        public readonly TaskCompletionSource Fin = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool EnJeu = true;
        public async Task DelayAsync(TimeSpan duration, CancellationToken ct)
        {
            await Fin.Task.WaitAsync(ct);
            EnJeu = false;
        }
    }

    // Le rejeu du journal passe avant la première réconciliation, même quand il attend la fin d'une partie.
    [Fact]
    public async Task Le_Worker_ne_reconcilie_pas_avant_la_fin_du_rejeu_du_journal()
    {
        var partie = new PartieEnCours();
        var rejeu = new RejeuAuDemarrage(new JournalDuService(null), NSubstitute.Substitute.For<DrDxt.Reconciler.Desinstallation.ILectureDesCibles>(),
            NSubstitute.Substitute.For<DrDxt.Core.IElevatedClient>(), jeuEnCours: () => partie.EnJeu, delai: partie);
        var (worker, tick) = Build(new FakeOsBuildProbe { Build = "22631.3447" },
            new InMemoryReconcilerStateStore(ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" }),
            new FakeUpdateSignal { Recent = false }, new FakeBootOutcomeStore(), new BlockingDelay(), new RecordingDiagnostics(),
            finDuRejeu: new FinDuRejeu(() => rejeu.Differe));

        await rejeu.StartAsync(CancellationToken.None);
        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);
        await Task.Delay(300);
        tick.Passes.Should().Be(0, "le rejeu attend encore la fin de la partie");

        partie.Fin.SetResult();
        await tick.WaitForPasses(1);

        cts.Cancel();
        await StopAsync(worker, run);
        tick.Passes.Should().BeGreaterThanOrEqualTo(1);
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
    // Rien ne se réapplique pendant une partie : la passe quotidienne attend la fin du jeu, relue toutes les 10 minutes.
    [Fact]
    public async Task DailyPoll_WaitsForTheGameToEnd()
    {
        var probe = new FakeOsBuildProbe { Build = "22631.3447" };
        var stateStore = new InMemoryReconcilerStateStore(
            ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" });
        var delay = new BlockingDelay();
        // Une partie à la première relecture, finie à la suivante : lu par la seule boucle, dans cet ordre.
        var reponses = new Queue<bool>([true, false]);
        var (worker, tick) = Build(probe, stateStore, new FakeUpdateSignal { Recent = false }, new FakeBootOutcomeStore(), delay,
            new RecordingDiagnostics(), () => reponses.Count > 0 && reponses.Dequeue());

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);
        await tick.WaitForPasses(1);

        await delay.ReleasePollOnce(PollInterval);
        tick.Passes.Should().Be(1, "en partie, la passe attend au lieu de tourner");
        await delay.ReleaseNext();
        await tick.WaitForPasses(2);

        cts.Cancel();
        await StopAsync(worker, run);
        delay.Attentes.Should().Contain(Worker.AttenteEnPartie);
        tick.Passes.Should().Be(2, "la partie finie, la passe tourne une fois");
    }

    // Même plafond pour la passe quotidienne : un faux jeu permanent ne l'empêche plus jamais de tourner.
    [Fact]
    public async Task DailyPoll_RunsAfterTheCapEvenIfTheGameNeverEnds()
    {
        var delay = new PartieSansFin();
        var (worker, tick) = Build(new FakeOsBuildProbe { Build = "22631.3447" },
            new InMemoryReconcilerStateStore(ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" }),
            new FakeUpdateSignal { Recent = false }, new FakeBootOutcomeStore(), delay, new RecordingDiagnostics(), () => true);

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);
        await tick.WaitForPasses(2);

        cts.Cancel();
        await StopAsync(worker, run);
        delay.EnPartie.Should().Be((int)(Worker.AttenteEnPartieMax / Worker.AttenteEnPartie));
    }

    // Une passe qui lève (manifeste, déclencheur, réconciliateur) ne fait pas tomber le service : journalisée, la boucle continue.
    [Fact]
    public async Task A_pass_that_throws_is_logged_and_the_service_keeps_running()
    {
        var passes = 0;
        var single = new SingleFlightReconcile(_ => ++passes == 1 ? throw new InvalidOperationException("manifeste") : Task.FromResult(Result.Ok()));
        var delay = new BlockingDelay();
        var diagnostics = new RecordingDiagnostics();
        var worker = new Worker(single,
            new FeatureUpdateTrigger(new FeatureUpdateDetector(new FakeOsBuildProbe { Build = "22631.3447" },
                new InMemoryReconcilerStateStore(ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" })),
                new FakeUpdateSignal { Recent = false }),
            new BootSessionMarker(new FakeBootOutcomeStore(), delay), diagnostics, delay,
            new HostTimingOptions(StableWindow, PollInterval), () => false);

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);
        await delay.ReleasePollOnce(PollInterval).WaitAsync(TimeSpan.FromSeconds(5));
        await delay.ProchaineAttenteDemandee().WaitAsync(TimeSpan.FromSeconds(5));

        passes.Should().Be(2, "la passe du démarrage a levé, la quotidienne a tourné quand même");
        run.IsFaulted.Should().BeFalse();
        diagnostics.Entries.Should().Contain(e => e.to == "pass-threw");
        cts.Cancel();
        await StopAsync(worker, run);
    }

    // Une annulation interne à la passe (un délai dépassé, un jeton qui n'est pas celui du service) n'est pas l'arrêt du
    // service : elle se dit comme une passe qui lève, et la boucle continue.
    [Fact]
    public async Task Une_annulation_interne_a_la_passe_n_arrete_pas_le_service()
    {
        var passes = 0;
        var single = new SingleFlightReconcile(_ => ++passes == 1 ? throw new TaskCanceledException("délai") : Task.FromResult(Result.Ok()));
        var delay = new BlockingDelay();
        var diagnostics = new RecordingDiagnostics();
        var worker = new Worker(single,
            new FeatureUpdateTrigger(new FeatureUpdateDetector(new FakeOsBuildProbe { Build = "22631.3447" },
                new InMemoryReconcilerStateStore(ReconcilerState.Default with { LastFeatureUpdateBuild = "22631.3447" })),
                new FakeUpdateSignal { Recent = false }),
            new BootSessionMarker(new FakeBootOutcomeStore(), delay), diagnostics, delay,
            new HostTimingOptions(StableWindow, PollInterval), () => false);

        var cts = new CancellationTokenSource();
        var run = worker.StartAsync(cts.Token);
        await delay.ReleasePollOnce(PollInterval).WaitAsync(TimeSpan.FromSeconds(5));
        await delay.ProchaineAttenteDemandee().WaitAsync(TimeSpan.FromSeconds(5));

        passes.Should().Be(2, "la passe du démarrage a été annulée de l'intérieur, la quotidienne a tourné quand même");
        diagnostics.Entries.Should().Contain(e => e.to == "pass-threw");
        cts.Cancel();
        await StopAsync(worker, run);
    }

    /// <summary>Un jeu qui ne finit jamais : chaque attente en partie passe tout de suite et se compte ; la première attente
    /// quotidienne passe, les suivantes et la fenêtre stable tiennent jusqu'à l'arrêt.</summary>
    private sealed class PartieSansFin : IDelay
    {
        public int EnPartie, Quotidiennes;
        public Task DelayAsync(TimeSpan duration, CancellationToken ct)
        {
            if (duration == Worker.AttenteEnPartie) { EnPartie++; return Task.CompletedTask; }
            if (duration == PollInterval && Interlocked.Increment(ref Quotidiennes) == 1) return Task.CompletedTask;
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private sealed class BlockingDelay : IDelay
    {
        private readonly object _lock = new();
        private TaskCompletionSource? _pendingPoll;
        private TaskCompletionSource _pollRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentQueue<TimeSpan> Attentes { get; } = new();

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
                Attentes.Enqueue(duration);
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

        // La boucle redemande une attente une fois sa passe finie : l'attendre sans la libérer fige le compte des passes.
        public Task ProchaineAttenteDemandee()
        {
            lock (_lock) { return _pollRequested.Task; }
        }

        // Attend la prochaine attente demandée (quotidienne ou en partie), puis l'achève.
        public async Task ReleaseNext()
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
        }
    }
}
