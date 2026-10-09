using DrDxt.Core;
using Microsoft.Extensions.Hosting;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// Worker (thin shell) — ties the host lifecycle to the TESTED classes.
//
// On start: mark the boot (dead-man +1) and run one reconcile pass unconditionally
// (the pass is idempotent — it re-applies only drifted items, a clean machine is a
// zero-write no-op — so re-running it on every boot is what makes a drifted registry /
// power / service setting persist across a normal reboot, not just after a feature update.
// GPU settings are NOT among them: this service runs with NVAPI/ADLX writes disabled, see
// ReconcilableTweakCatalog). Then schedule the stable-session reset after the up-window. Then
// loop on the daily backstop poll, reconciling unconditionally each interval. Every pass
// goes through SingleFlightReconcile so overlapping triggers never double-run.
//
// Feature-update detection (FeatureUpdateTrigger/Detector) is an ADDITIONAL signal, not
// a gate: each pass queries it for its recording side effect (edge-record
// LastFeatureUpdateBuild on an OS-build change) and journals a detected update, but
// reconciliation runs regardless of what it returns.
//
// IMPORTANT (architecture, T31): the Worker holds NO direct privileged-write
// dependency. It has no registry/service/power handle. The only way anything is
// written is INSIDE the reconcile pass, which delegates to the elevated helper via
// IElevatedReapplyClient (constructed deep in the reconciler graph, never injected
// here). The Worker's surface is: the single-flight tick runner, the feature-update
// trigger, the boot-session marker, the diagnostics log, and the timing config — all
// read/coordinate-only. This keeps the write path auditable and the guard test green.
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
        // (1) Mark this boot (dead-man +1), exactly once. A failure is logged, not fatal.
        var booted = await _bootMarker.MarkBootOnceAsync(stoppingToken).ConfigureAwait(false);
        if (!booted.IsOk)
            LogFailure("boot-mark", booted);

        // (2) Reconcile once now, unconditionally — boot is the primary drift window.
        await RunPassAsync(stoppingToken).ConfigureAwait(false);

        // (3) Schedule the dead-man stable reset after the stable up-window. Run it in
        // the background so the daily loop is not blocked; cancellation before the
        // window elapses (shutdown) correctly leaves the boot counted.
        var stableTask = _bootMarker.MarkStableAfterAsync(_timing.StableSessionWindow, stoppingToken);

        // (4) Daily backstop: poll on the configured interval until shutdown.
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

    /// <summary>Runs one reconcile pass unconditionally through the single-flight slot —
    /// the pass is idempotent (it re-applies only drifted items; a clean machine is a
    /// zero-write no-op), so it is safe and CORRECT to run on every trigger. This is what
    /// makes a drifted registry / power / service setting persist across a normal reboot, not
    /// just after a feature update (never a GPU setting: NVAPI/ADLX writes are disabled here).
    ///
    /// The feature-update trigger is queried as an ADDITIONAL signal, never a gate: it
    /// runs first purely for its recording side effect (the detector edge-records
    /// LastFeatureUpdateBuild on an OS-build change), and a detected update is journaled —
    /// but whatever it returns, the reconcile below still runs. A trigger-read failure or
    /// a reconcile failure is logged, never thrown out of the host loop.</summary>
    private async Task RunPassAsync(CancellationToken ct)
    {
        // Feature-update detection: an ADDITIONAL reason / recorder, not a gate. We call it
        // for its side effect (record LastFeatureUpdateBuild on a build change) and journal
        // a detected update, but we do not branch reconciliation on the result.
        var update = await _trigger.ShouldReconcileAsync(ct).ConfigureAwait(false);
        if (!update.IsOk)
            LogFailure("update-trigger", update);
        else if (update.Value)
            LogFeatureUpdate();

        // Reconcile unconditionally — every boot and every daily poll, regardless of the
        // feature-update signal. Single-flight serializes overlapping triggers (T29).
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
            // Cancelled before the stable window elapsed — boot stays counted (dead-man).
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

    /// <summary>Journals that a feature update was detected this pass. Informational: the
    /// reconcile runs regardless — this is the ADDITIONAL recorder, not a gate.</summary>
    private void LogFeatureUpdate()
        => _diagnostics.Change(
            tweakId: HostActors.ManifestTweakId,
            from: "feature-update",
            to: "detected",
            actor: HostActors.Security);
}

/// <summary>Host timing knobs (injected so tests / config can shrink them). The
/// stable-session window is the dead-man up-interval; the daily poll is the drift
/// backstop.</summary>
public sealed record HostTimingOptions(TimeSpan StableSessionWindow, TimeSpan DailyPollInterval)
{
    /// <summary>Production defaults: a 5-minute stable window, a 24-hour backstop poll.</summary>
    public static HostTimingOptions Default => new(TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
}
