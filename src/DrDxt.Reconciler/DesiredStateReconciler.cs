using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// Reconciler : l'IReconciler central (DrDxt.Core).
//
// À partir d'un manifeste vérifié (l'hôte le charge via IDesiredStateStore.LoadAsync, qui
// refuse au moindre doute en cas d'altération), il réconcilie élément par élément :
//   1. maintien en pause (PauseDuMaintien)            → aucune écriture, rien n'est comparé
//   2. levier hors allowlist / surface protégée       → refus + journal de sécurité (I3) ; puis un élément en pause
//                                                       → pas d'écriture (I7), on continue
//   3. lire l'actuel ; échec de sonde                  → élément KO (I2), on continue
//   4. actuel == voulu                                 → rien à faire (idempotence I1)
//   5. dérive → classer :
//        WindowsReset    → réappliquer par l'op typée existante, relire pour confirmer,
//                          journaliser le changement (I2 compare-and-set) ; les réglages GPU
//                          sont suspendus si la garde anti-boucle de démarrage le dit (T27b)
//        UserIntentional → notifier, ne pas écraser (I5)
//        Unknown         → prudence : notifier, ne pas écraser par défaut
//   6. bilan : un KO → Fail(RECONCILE_PARTIAL) avec la liste des ids KO ; sinon Ok.
//
// Sécurité : la réapplication ne passe jamais que par IElevatedReapplyClient, qui traduit un
// tweakId validé en une PrivilegedOp typée existante, jamais une nouvelle primitive
// privilégiée. Un tweakId hors de l'ensemble admis du manifeste vérifié est refusé avant tout IPC.
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
    private readonly PauseDuMaintien _pause;

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
        IClock clock,
        PauseDuMaintien pause)
    {
        _pause = pause;
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
    // Le point d'entrée de l'orchestration. Rend un Result et publie un ReconcileReport
    // structuré par la surcharge à part ci-dessous.
    // -------------------------------------------------------------------
    public async Task<Result> ReconcileAsync(DesiredState desired, CancellationToken ct)
    {
        var (result, _) = await ReconcileWithReportAsync(desired, ct);
        return result;
    }

    /// <summary>Comme <see cref="ReconcileAsync"/>, mais rend aussi le rapport structuré
    /// (ce qui a dérivé / été réappliqué / notifié / échoué). L'hôte s'en sert.</summary>
    public async Task<(Result result, ReconcileReport report)> ReconcileWithReportAsync(
        DesiredState desired, CancellationToken ct)
    {
        var stateRes = await _stateStore.LoadAsync(ct);
        // Un etat illisible etait remplace par l'etat par defaut (non suspendu) : la passe
        // reappliquait, puis ecrivait ce defaut par-dessus, et une pause du joueur disparaissait sur une
        // panne de lecture. Etat inconnu = rien d'ecrit, ni sur la machine ni dans l'etat. Un fichier abime, lui, ne revient pas
        // ici : le magasin le met de cote et repart du premier lancement (JsonReconcilerStateStore).
        if (!stateRes.IsOk)
            return (Result.Fail(stateRes.Error!.Value.Code,
                    Docteur.T("DSR.Illisible", stateRes.Error.Value.Message)),
                new ReconcileReport(GloballyPaused: false, Array.Empty<ReconcileItemResult>()));
        var state = stateRes.Value!;

        // La pause du maintien, posée par le joueur : rien comparer, rien écrire, ni sur la machine ni dans le sidecar.
        // Un fichier non fiable (abîmé, au propriétaire changé, ou posé par un programme du compte avant que l'installeur
        // ne durcisse la racine) vaut pause, l'échec sûr. Le journal le dit, l'App propose de la reposer ou de la retirer.
        var pause = _pause.Lire();
        if (pause == EtatDeLaPause.NonFiable)
            _log.Change(PauseDuMaintien.NomFichier, "?", "paused", "DrDXT/untrusted");
        if (pause != EtatDeLaPause.Actif)
            return (Result.Ok(), new ReconcileReport(GloballyPaused: true, Array.Empty<ReconcileItemResult>()));

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

        // Enregistrer la comptabilité du sidecar pour la passe suivante / l'interface. C'est le
        // sidecar modifiable, pas le manifeste signé. Au mieux : un échec d'enregistrement ne change
        // pas l'issue de la réconciliation (les écritures ont déjà eu lieu).
        await PersistBookkeepingAsync(results, ct);

        var final = failedIds.Count == 0
            ? Result.Ok()
            : Result.Fail(ReconcilerErrorCodes.ReconcilePartial,
                "Items failed to reconcile: " + string.Join(", ", failedIds));
        return (final, report);
    }

    private async Task PersistBookkeepingAsync(IReadOnlyList<ReconcileItemResult> results, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var currentBuild = _buildProbe.ReadCurrentBuild();
        var buildToken = currentBuild.IsOk ? currentBuild.Value! : string.Empty;

        // Le signal pilote se rafraichit ici, comme le build : c'est le seul endroit de `src/` qui ecrit
        // `LastSeenDriverVersion`. Recopie de l'item precedent, il resterait vide, et `Classify` (qui n'entre dans sa branche
        // pilote que s'il est renseigne) classerait `Unknown` au lieu de `WindowsReset` un reglage GPU remis a zero par une
        // mise a jour de pilote. Prouve par le test a deux passes (T8b/T8c posent la valeur a la main). Une lecture WMI par
        // vendeur, memorisee.
        //
        // Les sondes sont lues avant le verrou : il n'est tenu que le temps de relire l'état et de le réécrire. L'état est relu
        // sous le verrou, pas repris du début de la passe : une référence posée par le helper pendant la passe reste.
        var versionsParVendeur = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            var vendeur = _policy.VendorFor(r.TweakId);
            if (vendeur is not null && !versionsParVendeur.ContainsKey(vendeur))
            {
                var lu = _driverProbe.ReadDriverVersion(vendeur);
                versionsParVendeur[vendeur] = lu.IsOk ? lu.Value : null;
            }
        }

        await _stateStore.MettreAJourAsync(state => AvecLaComptabilite(state, results, now, buildToken, versionsParVendeur), ct);
    }

    private ReconcilerState AvecLaComptabilite(ReconcilerState state, IReadOnlyList<ReconcileItemResult> results,
        DateTimeOffset now, string buildToken, Dictionary<string, string?> versionsParVendeur)
    {
        var byId = state.Items.ToDictionary(i => i.TweakId, StringComparer.Ordinal);
        foreach (var r in results)
        {
            // Garder les drapeaux de l'élément existant (Paused) pour ne jamais écraser le
            // coupe-circuit ; ne rafraîchir que la comptabilité de réconciliation.
            byId.TryGetValue(r.TweakId, out var prev);

            var vendeur = _policy.VendorFor(r.TweakId);
            var versionPilote = vendeur is null ? null : versionsParVendeur[vendeur];

            byId[r.TweakId] = new ReconcilerItemState(
                TweakId: r.TweakId,
                Paused: prev?.Paused ?? false,
                LastSeenBuild: buildToken.Length > 0 ? buildToken : prev?.LastSeenBuild ?? string.Empty,
                // Meme prudence que pour le build : une lecture muette garde le jeton precedent
                // plutot que de l'effacer : effacer reviendrait a desarmer le signal a la premiere
                // passe ou WMI ne repond pas.
                LastSeenDriverVersion: string.IsNullOrEmpty(versionPilote)
                    ? prev?.LastSeenDriverVersion ?? string.Empty
                    : versionPilote,
                LastReconciledAt: now,
                LastResultCode: r.Outcome.ToString());
        }

        return state with { Items = byId.Values.ToArray() };
    }

    private async Task<ReconcileItemResult> ReconcileItemAsync(
        AppliedTweak tweak, ReconcilerState state, bool suspendGpu, CancellationToken ct)
    {
        var id = tweak.TweakId;

        // (2) Sécurité : seulement des surfaces côté OS, admises et non protégées.
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

        // (1) coupe-circuit par élément : lu pour le diagnostic, jamais de réapplication.
        if (item is { Paused: true })
        {
            var paused = await _probe.ReadCurrentValueAsync(id, ct);
            var cur = paused.IsOk ? paused.Value : null;
            _log.Change(id, cur ?? "?", tweak.DesiredValueJson, "DrDXT/paused");
            return new ReconcileItemResult(id, ItemOutcome.Skipped, null, cur, null);
        }

        // (3) lire la valeur actuelle.
        var read = await _probe.ReadCurrentValueAsync(id, ct);
        if (!read.IsOk)
            return new ReconcileItemResult(id, ItemOutcome.Failed, null, null, read.Error);

        var current = read.Value!;

        // (4) idempotence : déjà à la valeur voulue → aucune écriture, aucun Change (I1).
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
                // Prudence : notifier, ne jamais écraser en silence un choix de l'utilisateur.
                await _notifier.NotifyUserIntentionalDriftAsync(tweak, current, ct);
                _log.Change(id, current, tweak.DesiredValueJson, "DrDXT/notified");
                return new ReconcileItemResult(id, ItemOutcome.Notified, cause.Value, current, null);
        }
    }

    private async Task<ReconcileItemResult> ReapplyAsync(
        AppliedTweak tweak, string current, bool suspendGpu, CancellationToken ct)
    {
        var id = tweak.TweakId;

        // Anti-boucle de démarrage : un réglage GPU est suspendu tant que les démarrages sont malsains (T27b).
        if (suspendGpu && _policy.VendorFor(id) is not null)
        {
            _log.Change(id, current, tweak.DesiredValueJson, "DrDXT/suspended-bootloop");
            return new ReconcileItemResult(id, ItemOutcome.Suspended, DriftCause.WindowsReset, current, null);
        }

        // Réappliquer par l'op typée existante (jamais une nouvelle primitive privilégiée, I3).
        var applied = await _reapply.ReapplyAsync(id, tweak.DesiredValueJson, ct);
        if (!applied.IsOk)
            return new ReconcileItemResult(id, ItemOutcome.Failed, DriftCause.WindowsReset, current, applied.Error);

        // (compare-and-set) relire pour confirmer que l'écriture a vraiment pris.
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
    // ClassifyDriftAsync (contrat Core) : lit lui-même les signaux. N'écrit jamais.
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

    // La classification partagée et pure (aucune E/S au-delà des coutures de lecture déjà faites).
    private Result<DriftCause> Classify(
        AppliedTweak tweak, string current, ReconcilerItemState? item, CancellationToken ct)
    {
        var id = tweak.TweakId;

        // Aucune dérive → neutre, sans effet.
        if (string.Equals(current, tweak.DesiredValueJson, StringComparison.Ordinal))
            return Result<DriftCause>.Ok(DriftCause.Unknown);

        var isOriginal = string.Equals(current, tweak.OriginalValueJson, StringComparison.Ordinal);

        // La passe précédente trouvait déjà le réglage hors de la valeur de DrDXT (le joueur l'avait remis lui-même) : une
        // valeur d'origine vue après une mise à jour de Windows ou du pilote n'est pas une remise à zéro par Windows, c'est
        // le choix du joueur, et il est respecté.
        if (isOriginal && item is { LastResultCode: nameof(ItemOutcome.Notified) })
            return Result<DriftCause>.Ok(DriftCause.UserIntentional);

        // --- signal de version du pilote par constructeur (I8 / T8b/T8c) --- seulement quand la valeur est revenue à l'origine : une
        // valeur tierce après une mise à jour du pilote peut être celle de l'utilisateur, et dans le doute on la respecte.
        var vendor = _policy.VendorFor(id);
        if (vendor is not null)
        {
            var driver = _driverProbe.ReadDriverVersion(vendor);
            if (driver.IsOk && driver.Value is { } currentDriver
                && item is not null
                && !string.IsNullOrEmpty(item.LastSeenDriverVersion)
                && !string.Equals(currentDriver, item.LastSeenDriverVersion, StringComparison.OrdinalIgnoreCase)
                && isOriginal)
            {
                // Le constructeur a poussé un nouveau pilote depuis l'apply → cause non utilisateur.
                return Result<DriftCause>.Ok(DriftCause.WindowsReset);
            }
        }

        // --- signal du build de l'OS (principal, fiable) ---
        var buildRes = _buildProbe.ReadCurrentBuild();
        var buildChanged = buildRes.IsOk
            && item is not null
            && !string.IsNullOrEmpty(item.LastSeenBuild)
            && !string.Equals(buildRes.Value, item.LastSeenBuild, StringComparison.OrdinalIgnoreCase);

        if (buildChanged && isOriginal)
            return Result<DriftCause>.Ok(DriftCause.WindowsReset);

        // Pas de changement de build :
        if (!isOriginal)
            // Une valeur tierce posée par l'utilisateur → la respecter.
            return Result<DriftCause>.Ok(DriftCause.UserIntentional);

        // actuel == origine, pas de changement de build → ambigu.
        return Result<DriftCause>.Ok(DriftCause.Unknown);
    }

    private static ReconcilerItemState? FindItem(ReconcilerState state, string tweakId)
        => state.Items.FirstOrDefault(i => string.Equals(i.TweakId, tweakId, StringComparison.Ordinal));

    private static ReconcileItemResult Refused(string id, string code, string msg)
        => new(id, ItemOutcome.Refused, null, null, new Error(code, msg));
}
