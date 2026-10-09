using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// Reconciler — the core IReconciler (DrDxt.Core).
//
// Given a verified manifest (the host loads it via IDesiredStateStore.LoadAsync,
// which fails-closed on tamper), it reconciles item-by-item:
//   1. global kill-switch OR item paused           → skip write (I7), continue
//   2. tweak not allowlisted / protected surface    → refuse + security log (I3)
//   3. read current; probe fail                      → item KO (I2), continue
//   4. current == desired                            → NO-OP (idempotence I1)
//   5. drift → classify:
//        WindowsReset    → re-apply via existing typed op, read-BACK to confirm,
//                          log Change (I2 compare-and-set); GPU-tuning items are
//                          SUSPENDED if the boot-loop guard says so (T27b)
//        UserIntentional → NOTIFY, do not overwrite (I5)
//        Unknown         → conservative: notify, do not overwrite by default
//   6. aggregate: any KO → Fail(RECONCILE_PARTIAL) listing the KO ids; else Ok.
//
// SECURITY: re-apply only ever happens through IElevatedReapplyClient, which maps a
// validated tweakId to an existing typed PrivilegedOp — never a new privileged
// primitive. A tweakId not in the verified manifest's allowlisted set is refused
// before any IPC.
// -----------------------------------------------------------------------
public sealed class DesiredStateReconciler : IReconciler
{
    private readonly ISettingProbe _probe;
    private readonly IOsBuildProbe _buildProbe;
    private readonly IGpuDriverProbe _driverProbe;
    private readonly IReconcilerStateStore _stateStore;
    private readonly IElevatedReapplyClient _reapply;
    private readonly IReconcilerNotifier _notifier;
    private readonly IReconcilabilityPolicy _policy;
    private readonly IDiagnosticsLog _log;
    private readonly BootLoopGuard _bootGuard;
    private readonly IClock _clock;

    public DesiredStateReconciler(
        ISettingProbe probe,
        IOsBuildProbe buildProbe,
        IGpuDriverProbe driverProbe,
        IReconcilerStateStore stateStore,
        IElevatedReapplyClient reapply,
        IReconcilerNotifier notifier,
        IReconcilabilityPolicy policy,
        IDiagnosticsLog log,
        BootLoopGuard bootGuard,
        IClock clock)
    {
        _probe = probe;
        _buildProbe = buildProbe;
        _driverProbe = driverProbe;
        _stateStore = stateStore;
        _reapply = reapply;
        _notifier = notifier;
        _policy = policy;
        _log = log;
        _bootGuard = bootGuard;
        _clock = clock;
    }

    // -------------------------------------------------------------------
    // The orchestration entry point. Returns a Result AND publishes a structured
    // ReconcileReport via the out-of-band overload below.
    // -------------------------------------------------------------------
    public async Task<Result> ReconcileAsync(DesiredState desired, CancellationToken ct)
    {
        var (result, _) = await ReconcileWithReportAsync(desired, ct);
        return result;
    }

    /// <summary>Same as <see cref="ReconcileAsync"/> but also returns the structured
    /// report (what drifted / re-applied / notified / failed). Host uses this.</summary>
    public async Task<(Result result, ReconcileReport report)> ReconcileWithReportAsync(
        DesiredState desired, CancellationToken ct)
    {
        var stateRes = await _stateStore.LoadAsync(ct);
        // 26/09/2026 : un etat illisible etait remplace par l'etat par defaut (non suspendu) : la passe
        // reappliquait, puis ecrivait ce defaut par-dessus, et une pause du joueur disparaissait sur une
        // panne de lecture. Etat inconnu = rien d'ecrit, ni sur la machine ni dans l'etat.
        if (!stateRes.IsOk)
            return (Result.Fail(stateRes.Error!.Value.Code,
                    "État du réconciliateur illisible : rien n'est réappliqué. " + stateRes.Error.Value.Message),
                new ReconcileReport(GloballyPaused: false, Array.Empty<ReconcileItemResult>()));
        var state = stateRes.Value!;

        // Global kill-switch: compare nothing, write nothing, report paused (I7/T24).
        if (state.GlobalPaused)
        {
            var pausedReport = new ReconcileReport(GloballyPaused: true, Array.Empty<ReconcileItemResult>());
            return (Result.Ok(), pausedReport);
        }

        var suspendGpu = await _bootGuard.ShouldSuspendGpuReapplyAsync(ct);

        var results = new List<ReconcileItemResult>(desired.Tweaks.Count);
        var failedIds = new List<string>();

        foreach (var tweak in desired.Tweaks)
        {
            var r = await ReconcileItemAsync(tweak, state, suspendGpu, ct);
            results.Add(r);
            if (r.Outcome == ItemOutcome.Failed || r.Outcome == ItemOutcome.Refused)
                failedIds.Add(tweak.TweakId);
        }

        var report = new ReconcileReport(GloballyPaused: false, results);

        // Persist sidecar bookkeeping for the next pass / the UI (§5). This is the
        // mutable sidecar — not the signed manifest. Best-effort: a save failure does
        // not change the reconcile outcome (the writes already happened).
        await PersistBookkeepingAsync(state, results, ct);

        var final = failedIds.Count == 0
            ? Result.Ok()
            : Result.Fail(ReconcilerErrorCodes.ReconcilePartial,
                "Items failed to reconcile: " + string.Join(", ", failedIds));
        return (final, report);
    }

    private async Task PersistBookkeepingAsync(
        ReconcilerState state, IReadOnlyList<ReconcileItemResult> results, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var currentBuild = _buildProbe.ReadCurrentBuild();
        var buildToken = currentBuild.IsOk ? currentBuild.Value! : string.Empty;

        // Corrige LE 11/09/2026 — le signal pilote ne s'etait jamais declenche.
        //
        // Cette methode rafraichissait `LastSeenBuild` depuis la sonde, mais RECOPIAIT
        // `LastSeenDriverVersion` depuis l'item precedent. Or aucun autre endroit de `src/`
        // n'ecrivait jamais ce champ : il naissait vide et le restait a perpetuite. Et
        // `Classify` garde sa branche pilote derriere `!string.IsNullOrEmpty(item.LastSeenDriverVersion)`
        // — donc un reglage GPU remis a zero par une mise a jour de pilote n'etait jamais
        // classe `WindowsReset` : il tombait en `Unknown` et etait NOTIFIE au lieu d'etre
        // reapplique. La revalidation apres reboot marchait (le build, lui, est ecrit) ; la
        // revalidation apres changement de pilote etait une branche morte.
        //
        // Les tests T8b/T8c etaient VERTS pendant ce temps : ils posaient `lastDriver` a la main
        // dans l'etat initial, une valeur que le produit ne produisait pas. Un test peut nommer une
        // garantie sans la prouver — d'ou le test a deux passes qui accompagne cette correction.
        //
        // Une lecture par VENDEUR, memorisee : un item sans vendeur n'en demande aucune, et dix
        // items NVIDIA n'en demandent qu'une (la sonde reelle interroge WMI).
        var versionsParVendeur = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var byId = state.Items.ToDictionary(i => i.TweakId, StringComparer.Ordinal);
        foreach (var r in results)
        {
            // Preserve the existing item's flags (Paused) so we never clobber the
            // kill-switch; only refresh the reconcile bookkeeping.
            byId.TryGetValue(r.TweakId, out var prev);

            var vendeur = _policy.VendorFor(r.TweakId);
            string? versionPilote = null;
            if (vendeur is not null && !versionsParVendeur.TryGetValue(vendeur, out versionPilote))
            {
                var lu = _driverProbe.ReadDriverVersion(vendeur);
                versionPilote = lu.IsOk ? lu.Value : null;
                versionsParVendeur[vendeur] = versionPilote;
            }

            byId[r.TweakId] = new ReconcilerItemState(
                TweakId: r.TweakId,
                Paused: prev?.Paused ?? false,
                LastSeenBuild: buildToken.Length > 0 ? buildToken : prev?.LastSeenBuild ?? string.Empty,
                // Meme prudence que pour le build : une lecture muette garde le jeton precedent
                // plutot que de l'effacer — effacer reviendrait a desarmer le signal a la premiere
                // passe ou WMI ne repond pas.
                LastSeenDriverVersion: string.IsNullOrEmpty(versionPilote)
                    ? prev?.LastSeenDriverVersion ?? string.Empty
                    : versionPilote,
                LastReconciledAt: now,
                LastResultCode: r.Outcome.ToString());
        }

        var updated = state with { Items = byId.Values.ToArray() };
        await _stateStore.SaveAsync(updated, ct);
    }

    private async Task<ReconcileItemResult> ReconcileItemAsync(
        AppliedTweak tweak, ReconcilerState state, bool suspendGpu, CancellationToken ct)
    {
        var id = tweak.TweakId;

        // (2) SECURITY: only allowlisted, non-protected, OS-side surfaces (I3, T20/T22).
        if (_policy.IsProtected(id))
        {
            _log.Change(id, "?", "?", "DrDXT/refused-protected");
            return Refused(id, ReconcilerErrorCodes.ProtectedSurface,
                $"'{id}' targets a protected surface and is never touched.");
        }

        if (!_policy.IsReconcilable(id))
        {
            _log.Change(id, "?", "?", "DrDXT/refused-not-allowlisted");
            return Refused(id, ReconcilerErrorCodes.TweakNotAllowlisted,
                $"'{id}' is not an allowlisted reconcilable tweak.");
        }

        var item = FindItem(state, id);

        // (1) per-item kill-switch: read for diagnostics, never re-apply (I7/T23).
        if (item is { Paused: true })
        {
            var paused = await _probe.ReadCurrentValueAsync(id, ct);
            var cur = paused.IsOk ? paused.Value : null;
            _log.Change(id, cur ?? "?", tweak.DesiredValueJson, "DrDXT/paused");
            return new ReconcileItemResult(id, ItemOutcome.Skipped, null, cur, null);
        }

        // (3) read current value.
        var read = await _probe.ReadCurrentValueAsync(id, ct);
        if (!read.IsOk)
            return new ReconcileItemResult(id, ItemOutcome.Failed, null, null, read.Error);

        var current = read.Value!;

        // (4) idempotence: already desired → no write, no Change (I1).
        if (string.Equals(current, tweak.DesiredValueJson, StringComparison.Ordinal))
            return new ReconcileItemResult(id, ItemOutcome.NoOp, null, current, null);

        // (5) drift → classify.
        var cause = Classify(tweak, current, item, ct);
        if (!cause.IsOk)
            return new ReconcileItemResult(id, ItemOutcome.Failed, null, current, cause.Error);

        switch (cause.Value)
        {
            case DriftCause.WindowsReset:
                return await ReapplyAsync(tweak, current, suspendGpu, ct);

            case DriftCause.UserIntentional:
            case DriftCause.Unknown:
            default:
                // Conservative: notify, never overwrite a user choice silently (I5/§16).
                await _notifier.NotifyUserIntentionalDriftAsync(tweak, current, ct);
                _log.Change(id, current, tweak.DesiredValueJson, "DrDXT/notified");
                return new ReconcileItemResult(id, ItemOutcome.Notified, cause.Value, current, null);
        }
    }

    private async Task<ReconcileItemResult> ReapplyAsync(
        AppliedTweak tweak, string current, bool suspendGpu, CancellationToken ct)
    {
        var id = tweak.TweakId;

        // Anti boot-loop: a GPU-tuning item is suspended while boots are unhealthy (T27b).
        if (suspendGpu && _policy.VendorFor(id) is not null)
        {
            _log.Change(id, current, tweak.DesiredValueJson, "DrDXT/suspended-bootloop");
            return new ReconcileItemResult(id, ItemOutcome.Suspended, DriftCause.WindowsReset, current, null);
        }

        // Re-apply via the existing typed op (never a new privileged primitive, I3).
        var applied = await _reapply.ReapplyAsync(id, tweak.DesiredValueJson, ct);
        if (!applied.IsOk)
            return new ReconcileItemResult(id, ItemOutcome.Failed, DriftCause.WindowsReset, current, applied.Error);

        // (compare-and-set) read-BACK to confirm the write actually took (T21).
        var confirm = await _probe.ReadCurrentValueAsync(id, ct);
        if (!confirm.IsOk)
            return new ReconcileItemResult(id, ItemOutcome.Failed, DriftCause.WindowsReset, current, confirm.Error);

        if (!string.Equals(confirm.Value, tweak.DesiredValueJson, StringComparison.Ordinal))
        {
            var err = new Error(ReconcilerErrorCodes.ConfirmFailed,
                $"'{id}' write reported OK but value not confirmed (still '{confirm.Value}').");
            return new ReconcileItemResult(id, ItemOutcome.Failed, DriftCause.WindowsReset, confirm.Value, err);
        }

        _log.Change(id, current, tweak.DesiredValueJson, "DrDXT");
        return new ReconcileItemResult(id, ItemOutcome.Reapplied, DriftCause.WindowsReset, confirm.Value, null);
    }

    // -------------------------------------------------------------------
    // ClassifyDriftAsync (Core contract) — reads the signals itself. Never writes.
    // -------------------------------------------------------------------
    public async Task<Result<DriftCause>> ClassifyDriftAsync(AppliedTweak tweak)
    {
        var read = await _probe.ReadCurrentValueAsync(tweak.TweakId, CancellationToken.None);
        if (!read.IsOk)
            return Result<DriftCause>.Fail(read.Error!.Value.Code, read.Error.Value.Message);

        var stateRes = await _stateStore.LoadAsync(CancellationToken.None);
        var state = stateRes.IsOk ? stateRes.Value! : ReconcilerState.Default;
        var item = FindItem(state, tweak.TweakId);

        return Classify(tweak, read.Value!, item, CancellationToken.None);
    }

    // The shared, pure classification (no IO beyond the read seams already done).
    private Result<DriftCause> Classify(
        AppliedTweak tweak, string current, ReconcilerItemState? item, CancellationToken ct)
    {
        var id = tweak.TweakId;

        // No drift at all → neutral, no effect (T11).
        if (string.Equals(current, tweak.DesiredValueJson, StringComparison.Ordinal))
            return Result<DriftCause>.Ok(DriftCause.Unknown);

        // --- per-vendor driver-version signal (I8 / T8b/T8c) ---
        var vendor = _policy.VendorFor(id);
        if (vendor is not null)
        {
            var driver = _driverProbe.ReadDriverVersion(vendor);
            if (driver.IsOk && driver.Value is { } currentDriver
                && item is not null
                && !string.IsNullOrEmpty(item.LastSeenDriverVersion)
                && !string.Equals(currentDriver, item.LastSeenDriverVersion, StringComparison.OrdinalIgnoreCase))
            {
                // The vendor pushed a new driver since apply → non-user cause.
                return Result<DriftCause>.Ok(DriftCause.WindowsReset);
            }
        }

        // --- OS build signal (primary, reliable) ---
        var buildRes = _buildProbe.ReadCurrentBuild();
        var buildChanged = buildRes.IsOk
            && item is not null
            && !string.IsNullOrEmpty(item.LastSeenBuild)
            && !string.Equals(buildRes.Value, item.LastSeenBuild, StringComparison.OrdinalIgnoreCase);

        var isOriginal = string.Equals(current, tweak.OriginalValueJson, StringComparison.Ordinal);

        if (buildChanged && isOriginal)
            return Result<DriftCause>.Ok(DriftCause.WindowsReset);

        // No build change:
        if (!isOriginal)
            // A third value the user set → respect it (T9).
            return Result<DriftCause>.Ok(DriftCause.UserIntentional);

        // current == original, no build change → ambiguous (T10).
        return Result<DriftCause>.Ok(DriftCause.Unknown);
    }

    private static ReconcilerItemState? FindItem(ReconcilerState state, string tweakId)
        => state.Items.FirstOrDefault(i => string.Equals(i.TweakId, tweakId, StringComparison.Ordinal));

    private static ReconcileItemResult Refused(string id, string code, string msg)
        => new(id, ItemOutcome.Refused, null, null, new Error(code, msg));
}
