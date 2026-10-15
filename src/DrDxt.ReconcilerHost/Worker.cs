using DrDxt.Core;
using Microsoft.Extensions.Hosting;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// Worker (coquille mince) : relie le cycle de vie de l'hôte aux classes testées.
//
// Au démarrage : marquer le démarrage (homme mort +1) et lancer une passe de réconciliation sans
// condition (la passe est idempotente : elle ne réapplique que les éléments qui ont dérivé, une
// machine propre est un tour sans écriture ; la relancer à chaque démarrage est donc ce qui fait
// tenir un réglage de registre / d'alimentation / de service qui a dérivé à travers un
// redémarrage normal, pas seulement après une mise à jour de fonctionnalité. Les réglages GPU n'en
// font pas partie : ce service tourne avec les écritures NVAPI/ADLX désactivées, voir
// ReconcilableTweakCatalog). Puis planifier la remise à zéro de session stable après la fenêtre
// de marche. Puis boucler sur l'interrogation quotidienne de secours, en réconciliant sans
// condition à chaque intervalle. Chaque passe passe par SingleFlightReconcile : des déclencheurs
// qui se chevauchent ne tournent jamais deux fois.
//
// La détection de mise à jour de fonctionnalité (FeatureUpdateTrigger/Detector) est un signal en
// plus, pas une barrière : chaque passe l'interroge pour son effet d'enregistrement (relevé sur
// front de LastFeatureUpdateBuild sur un changement de build de l'OS) et journalise une mise à
// jour détectée, mais la réconciliation tourne quoi qu'il rende.
//
// Important (architecture) : le Worker n'a aucune dépendance directe d'écriture privilégiée. Il
// n'a aucun handle de registre, de service ou d'alimentation. La seule façon d'écrire quoi que ce
// soit est dans la passe de réconciliation : DesiredStateReconciler → IElevatedReapplyClient →
// l'IElevatedClient de l'hôte, qui est l'exécuteur en processus (InProcessElevatedClient derrière
// OpAllowlist, option B), sans IPC vers le helper (construit au fond du graphe, jamais injecté ici). La
// surface du Worker : le lanceur de tour à vol unique, le déclencheur de mise à jour de
// fonctionnalité, le marqueur de session de démarrage, le journal de diagnostic et la config de
// temps, tous en lecture / coordination seulement. Le chemin d'écriture reste ainsi auditable et
// le test de garde vert.
// -----------------------------------------------------------------------
public sealed class Worker : BackgroundService
{
    private readonly SingleFlightReconcile _single;
    private readonly FeatureUpdateTrigger _trigger;
    private readonly BootSessionMarker _bootMarker;
    private readonly IDiagnosticsLog _diagnostics;
    private readonly IDelay _delay;
    private readonly HostTimingOptions _timing;

    public Worker(
        SingleFlightReconcile single,
        FeatureUpdateTrigger trigger,
        BootSessionMarker bootMarker,
        IDiagnosticsLog diagnostics,
        IDelay delay,
        HostTimingOptions timing)
    {
        _single = single;
        _trigger = trigger;
        _bootMarker = bootMarker;
        _diagnostics = diagnostics;
        _delay = delay;
        _timing = timing;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // (1) Marquer ce démarrage (homme mort +1), exactement une fois. Un échec est journalisé, pas fatal.
        var booted = await _bootMarker.MarkBootOnceAsync(stoppingToken).ConfigureAwait(false);
        if (!booted.IsOk)
            LogFailure("boot-mark", booted);

        // (2) Réconcilier une fois maintenant, sans condition : le démarrage est la principale fenêtre de dérive.
        await RunPassAsync(stoppingToken).ConfigureAwait(false);

        // (3) Planifier la remise à zéro « homme mort » après la fenêtre de marche stable. La lancer en
        // arrière-plan pour ne pas bloquer la boucle quotidienne ; une annulation avant la fin de la
        // fenêtre (arrêt) laisse à raison le démarrage compté.
        var stableTask = _bootMarker.MarkStableAfterAsync(_timing.StableSessionWindow, stoppingToken);

        // (4) Secours quotidien : interroger à l'intervalle configuré jusqu'à l'arrêt.
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _delay.DelayAsync(_timing.DailyPollInterval, stoppingToken).ConfigureAwait(false);
                await RunPassAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        await AwaitStableTaskAsync(stableTask).ConfigureAwait(false);
    }

    /// <summary>Lance une passe de réconciliation sans condition dans le créneau unique : la passe est
    /// idempotente (elle ne réapplique que les éléments qui ont dérivé ; une machine propre est un
    /// tour sans écriture), il est donc sûr et juste de la lancer à chaque déclencheur. C'est ce qui
    /// fait tenir un réglage de registre / d'alimentation / de service qui a dérivé à travers un
    /// redémarrage normal, pas seulement après une mise à jour de fonctionnalité (jamais un réglage
    /// GPU : les écritures NVAPI/ADLX sont désactivées ici).
    ///
    /// Le déclencheur de mise à jour de fonctionnalité est interrogé comme signal en plus, jamais
    /// comme barrière : il tourne d'abord pour son seul effet d'enregistrement (le détecteur relève
    /// sur front LastFeatureUpdateBuild sur un changement de build de l'OS), et une mise à jour
    /// détectée est journalisée ; mais quoi qu'il rende, la réconciliation ci-dessous tourne. Un échec
    /// de lecture du déclencheur ou de réconciliation est journalisé, jamais levé hors de la boucle de l'hôte.</summary>
    private async Task RunPassAsync(CancellationToken ct)
    {
        // Détection de mise à jour de fonctionnalité : une raison / un enregistreur en plus, pas une
        // barrière. On l'appelle pour son effet (enregistrer LastFeatureUpdateBuild sur un changement de
        // build) et on journalise une mise à jour détectée, mais la réconciliation ne dépend pas du résultat.
        var update = await _trigger.ShouldReconcileAsync(ct).ConfigureAwait(false);
        if (!update.IsOk)
            LogFailure("update-trigger", update);
        else if (update.Value)
            LogFeatureUpdate();

        // Réconcilier sans condition : à chaque démarrage et chaque interrogation quotidienne, quel que
        // soit le signal de mise à jour de fonctionnalité. Le vol unique met en file les déclencheurs qui se chevauchent.
        var pass = await _single.RunTickAsync(ct).ConfigureAwait(false);
        if (!pass.IsOk)
            LogFailure("reconcile", pass);
    }

    private static async Task AwaitStableTaskAsync(Task<Result> stableTask)
    {
        try
        {
            await stableTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Annulé avant la fin de la fenêtre stable : le démarrage reste compté (homme mort).
        }
    }

    private void LogFailure(string stage, Result result)
    {
        var error = result.Error;
        _diagnostics.Change(
            tweakId: HostActors.ManifestTweakId,
            from: error?.Code ?? "unknown",
            to: stage + "-failed",
            actor: HostActors.Security);
    }

    /// <summary>Journalise qu'une mise à jour de fonctionnalité a été détectée à cette passe. Pour
    /// information : la réconciliation tourne de toute façon ; c'est l'enregistreur en plus, pas une barrière.</summary>
    private void LogFeatureUpdate()
        => _diagnostics.Change(
            tweakId: HostActors.ManifestTweakId,
            from: "feature-update",
            to: "detected",
            actor: HostActors.Security);
}

/// <summary>Réglages de temps de l'hôte (injectés pour que les tests / la config les réduisent).
/// La fenêtre de session stable est l'intervalle de marche « homme mort » ; l'interrogation
/// quotidienne est le secours contre la dérive.</summary>
public sealed record HostTimingOptions(TimeSpan StableSessionWindow, TimeSpan DailyPollInterval)
{
    /// <summary>Défauts de production : une fenêtre stable de 5 minutes, une interrogation de secours toutes les 24 heures.</summary>
    public static HostTimingOptions Default => new(TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
}
