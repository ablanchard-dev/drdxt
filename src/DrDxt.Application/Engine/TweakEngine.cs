using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Measurement;

namespace DrDxt.Application.Engine;

// -----------------------------------------------------------------------
// TweakEngine — the transactional heart of M2 (master plan §3/§10, plan 02 C1+C2).
//
// It ORCHESTRATES; it holds no privileged primitive itself. Every privileged
// write is performed by the tweaks (through IElevatedClient, Phase A) and every
// rollback / auto-revert is performed by the TWEAK's own RevertAsync — so a
// composite-snapshot tweak (X3D CCD fix, VRR cap) is fully restored, not just its
// first op (golden rule §6). The engine still depends on ISafetyManager only to
// EnsureRestorePointAsync (Phase B); SafetyManager.RevertAsync stays the standalone
// dashboard revert path for a persisted snapshot without a tweak instance.
//
// Contracts: the canonical reconciled forms from Core §10 are used as-is —
//   TweakPlan(IReadOnlyList<string> TweakIds)
//   ApplyOutcome(string TweakId, bool Applied, bool AutoReverted, TweakSnapshot? Snapshot, Error? Error)
//   ApplyReport(IReadOnlyList<ApplyOutcome> Outcomes, AbVerdict? Verdict)
//   ITweakEngine.ApplyAsync(TweakPlan, BenchmarkResult?, int targetPid, CancellationToken)
// The C-phase proposal's batch-level fields map onto these: AutoRevertedIds =
// Outcomes.Where(AutoReverted), SummaryKey = Verdict.SummaryKey, Applied snapshots
// = Outcomes.Where(Applied).Snapshot. The gating "Plan(...)" lives on the concrete
// engine (EnginePlan) — kept off the Core interface so §10's authoritative shape
// is untouched.
//
// Invariants honored:
//  - never a half-applied batch: first apply failure ⇒ LIFO-revert everything
//    already applied ⇒ ENGINE.BATCH_REVERTED.
//  - nothing applied without a verified restore point (EnsureRestorePointAsync).
//  - Honest measurement: only a MEASURED regression beyond combined margins
//    auto-reverts; "no measurable gain" is a VALID kept state; a gain is reported
//    only when measured. No baseline / bench failure ⇒ keep, null verdict (never
//    invent a gain, never revert a change we have no measured reason to revert).
//  - Result everywhere; NO exception escapes (frontier try/catch).
// -----------------------------------------------------------------------
public sealed class TweakEngine : ITweakEngine
{
    // The duration / runs of the post-apply re-benchmark. Conservative defaults;
    // the real values are driven by UX (progress bar) — kept here as a single knob.
    private static readonly TimeSpan ReBenchDuration = TimeSpan.FromSeconds(10);
    // ≥3 runs so the inter-run A/B margin is finite and meaningful (NS-6 §6): the auto-revert is
    // an A/B verdict, and below 3 runs the margin is "unreliable" (∞) → no regression (or gain)
    // could ever be proven, which would defeat the measured auto-revert.
    private const int ReBenchRuns = 3;

    private const string SummaryRegressionReverted = "verdict.regression_reverted";
    private const string SummaryNoMeasurableGain   = "verdict.no_measurable_gain";
    private const string SummaryMeasurableGain     = "verdict.measurable_gain";

    private readonly ISafetyManager _safety;
    private readonly IBenchmarkRunner _bench;
    private readonly IDiagnosticsLog _diag;
    private readonly ITweakCatalog _catalog;

    /// <summary>Le lecteur de registre qui sert a prouver qu'un levier est en place. Optionnel et
    /// en queue : sans lui, la verification rend <c>Unknown</c> et le moteur se comporte comme
    /// avant — mais alors il ne peut plus affirmer qu'un levier est applique.</summary>
    private readonly IRegistryReader? _registre;

    /// <summary>Le pilote NVIDIA, pour relire un reglage DRS applique. Optionnel : sans lui, un
    /// levier pilote rend `Unavailable` — donc aucun gain ne lui sera attribue.</summary>
    private readonly INvApi? _nvApi;

    /// <summary>Le lecteur de plan d'alimentation actif, meme role que les deux precedents.</summary>
    private readonly IPowerPlanReader? _alimentation;

    public TweakEngine(ISafetyManager safety, IBenchmarkRunner bench, IDiagnosticsLog diag,
        ITweakCatalog catalog, IRegistryReader? registre = null, INvApi? nvApi = null,
        IPowerPlanReader? alimentation = null)
    {
        _safety = safety;
        _bench = bench;
        _diag = diag;
        _catalog = catalog;
        _registre = registre;
        _nvApi = nvApi;
        _alimentation = alimentation;
    }

    /// <summary>Gating (master plan §16): keep the tweaks whose AppliesTo(hw,state)
    /// is true, collect the ids that were skipped because they do not apply to this
    /// machine. Pure, side-effect-free — the UI uses it to say "this module does not
    /// apply to your machine" instead of offering the impossible.</summary>
    public EnginePlan Plan(IReadOnlyList<ITweak> all, HardwareInventory hw, SettingsState state)
    {
        var applicable = new List<ITweak>();
        var skipped = new List<string>();
        foreach (var t in all)
        {
            if (t.AppliesTo(hw, state))
                applicable.Add(t);
            else
                skipped.Add(t.Id);
        }
        return new EnginePlan(applicable, skipped);
    }

    public async Task<Result<ApplyReport>> ApplyAsync(
        TweakPlan plan, BenchmarkResult? baseline, int targetPid, CancellationToken ct)
    {
        try
        {
            return await ApplyCoreAsync(plan, baseline, targetPid, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Frontier guard: nothing exceptional ever escapes the engine.
            return Result<ApplyReport>.Fail(ErrorCodes.BatchReverted, $"Engine boundary error: {ex.Message}");
        }
    }

    private async Task<Result<ApplyReport>> ApplyCoreAsync(
        TweakPlan plan, BenchmarkResult? baseline, int targetPid, CancellationToken ct)
    {
        // Resolve plan ids → tweaks; ids missing from the catalog are skipped cleanly.
        var tweaks = new List<ITweak>();
        foreach (var id in plan.TweakIds)
        {
            var got = _catalog.Get(id);
            if (got.IsOk && got.Value is not null)
                tweaks.Add(got.Value);
        }

        // Nothing to do: don't even create a restore point (B1: no spamming).
        if (tweaks.Count == 0)
            return Result<ApplyReport>.Ok(new ApplyReport(Array.Empty<ApplyOutcome>(), Verdict: null));

        // (1) A verified restore point must exist before any apply. Fail ⇒ abort, apply nothing.
        var point = await _safety.EnsureRestorePointAsync().ConfigureAwait(false);
        if (!point.IsOk)
            return Result<ApplyReport>.Fail(point.Error!.Value.Code, point.Error!.Value.Message);

        // (2) Apply each tweak, pushing its snapshot on a LIFO stack. First failure
        //     ⇒ roll the whole batch back (LIFO) ⇒ ENGINE.BATCH_REVERTED. Never partial.
        var applied = new Stack<(ITweak Tweak, TweakSnapshot Snapshot)>();
        // Vrai tant que chaque levier applique a prouve que sa cible est en place. Un seul
        // `Unknown` suffit a interdire d'attribuer la mesure au batch.
        var toutProuve = true;
        foreach (var tweak in tweaks)
        {
            Result<TweakSnapshot> applyResult;
            try
            {
                applyResult = await tweak.ApplyAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                applyResult = Result<TweakSnapshot>.Fail(ErrorCodes.HelperOpFailed, $"Apply threw: {ex.Message}");
            }

            if (!applyResult.IsOk || applyResult.Value is null)
                return await RollbackBatchAsync(applied, tweak.Id, applyResult.Error).ConfigureAwait(false);

            var snapshot = applyResult.Value;

            // (2b) prouver que la cible est en place, avant toute mesure.
            //
            // Un `Result.Ok` de l'helper dit que l'op a ete ENVOYEE, pas qu'elle a PRIS. Une
            // feuille protegee par une strategie de groupe, un pilote qui clampe, un autre outil
            // qui reecrit derriere : dans les trois cas l'apply « reussit » et la valeur cible
            // n'est pas la. Le moteur mesurait alors l'ecart d'une session et l'attribuait a un
            // changement inexistant — c'est ainsi qu'on fabrique un faux gain.
            //
            // Deux sorties, et elles ne se confondent pas.
            //
            // `NotApplied` et `Drifted` sont des ECHECS : dans le premier cas on sait que rien n'a
            // bouge, dans le second la feuille porte une troisieme valeur — quelqu'un d'autre
            // ecrit dessus. Les deux annulent le batch.
            //
            // `Unknown` et `Unavailable` (op non relisable, lecteur absent, snapshot ancien) ne
            // condamnent pas le levier : un plan d'alimentation ou une courbe de ventilation ne se
            // relisent pas dans le registre, et ils fonctionnent tres bien. Mais ils privent le
            // produit du droit d'ATTRIBUER une mesure a ce levier — donc le batch est garde et
            // l'A/B est saute, exactement comme pour un changement qui n'agit qu'apres redemarrage.
            if (_registre is not null)
            {
                var preuve = AppliedStateVerifier.Verifier(snapshot, _registre, _nvApi, _alimentation);
                _diag.Change(tweak.Id, preuve.RequestedState ?? "?",
                    $"readback:{preuve.Verdict}", "DrDXT");

                if (preuve.Verdict is EtatApplique.NotApplied or EtatApplique.Drifted)
                {
                    applied.Push((tweak, snapshot));   // pour que le rollback le defasse aussi
                    var pourquoi = preuve.Verdict == EtatApplique.NotApplied
                        ? $"la valeur lue est restée celle d'avant ({preuve.ObservedState ?? "absente"})"
                        : $"la valeur lue ({preuve.ObservedState ?? "absente"}) n'est ni la cible ni "
                          + "l'état d'avant : une autre source écrit sur cette feuille";
                    return await RollbackBatchAsync(applied, tweak.Id,
                        new Error(ErrorCodes.TweakSdkUnavailable,
                            $"{tweak.Id} : {pourquoi} alors que {preuve.RequestedState} était "
                            + "demandé. Aucun banc ne sera lancé sur un levier qui n'a pas pris."))
                        .ConfigureAwait(false);
                }

                if (!preuve.AutoriseLaMesure) toutProuve = false;
            }
            else
            {
                // Sans lecteur, aucune cible n'est prouvable : on garde, on ne mesure pas.
                toutProuve = false;
            }

            applied.Push((tweak, snapshot));
            // Audit log: a change DrDXT made (from = original snapshot payload, to = applied).
            _diag.Change(tweak.Id, snapshot.OriginalValueJson, "applied", "DrDXT");
        }

        // (3) All applied OK. Re-measure if we can; auto-revert only on a MEASURED regression.
        return await EvaluateAndFinalizeAsync(applied, baseline, targetPid, toutProuve, ct).ConfigureAwait(false);
    }

    // --- C1: LIFO rollback of an apply-time batch failure (never throws). ---
    private async Task<Result<ApplyReport>> RollbackBatchAsync(
        Stack<(ITweak Tweak, TweakSnapshot Snapshot)> applied, string failedId, Error? cause)
    {
        var unreverted = new List<string>();
        while (applied.Count > 0)
        {
            var (tweak, snapshot) = applied.Pop();   // LIFO: last applied is reverted first
            Result revert;
            try
            {
                // The TWEAK's own revert (not _safety) so a COMPOSITE snapshot is fully
                // restored: the single-op tweaks delegate to the same generic decode, so
                // behavior is unchanged for them, but X3D-CCD / VRR-cap reverts replay
                // their whole composite — reversibility gap closed (golden rule §6).
                revert = await tweak.RevertAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                revert = Result.Fail(ErrorCodes.HelperOpFailed, ex.Message);
            }

            if (revert.IsOk)
            {
                _diag.Change(tweak.Id, "applied", "reverted", "DrDXT");
            }
            else
            {
                // A revert that itself failed during rollback: log CRITICAL and keep the
                // id — the ultimate net is the system restore point. We do not throw.
                unreverted.Add(tweak.Id);
                _diag.Change(tweak.Id, "applied", "revert-FAILED", "DrDXT");
            }
        }

        // La cause survit au rollback. Ce message ne portait que l'identifiant et le code :
        // « apply of 'x' failed (TWEAK.SDK_UNAVAILABLE) ». Or c'est exactement ici que le produit
        // doit savoir pourquoi — feuille protegee, valeur clampee par le pilote, valeur lue restee
        // celle d'avant. Un diagnostic qui perd sa cause au moment de l'annuler ne vaut pas mieux
        // qu'une erreur generique.
        var detail = $"apply of '{failedId}' failed ({cause?.Code})";
        if (!string.IsNullOrWhiteSpace(cause?.Message))
            detail += $": {cause?.Message}";
        if (unreverted.Count > 0)
            detail += $"; rollback could not revert: {string.Join(", ", unreverted)} (use the System Restore point)";

        return Result<ApplyReport>.Fail(ErrorCodes.BatchReverted, detail);
    }

    // --- C2: re-bench → AbComparer significance → keep or auto-revert. ---
    private async Task<Result<ApplyReport>> EvaluateAndFinalizeAsync(
        Stack<(ITweak Tweak, TweakSnapshot Snapshot)> applied,
        BenchmarkResult? baseline, int targetPid, bool cibleProuvee, CancellationToken ct)
    {
        // Applied in push order (oldest first) for a stable, readable report.
        var appliedList = applied.Reverse().ToList();

        // Reboot-required batch (HAGS, Game Mode, …): the change does not take effect until
        // the next boot / game launch, so an immediate re-bench can't reflect it — and session
        // noise must never auto-revert a change whose effect isn't even live yet. When the WHOLE
        // batch is reboot-required, skip the A/B entirely and keep it; the gain is proven by the
        // next in-game scan (honest deferred proof, §22.3). heuristic: a MIXED batch still re-
        // benches (the immediate tweaks are measured); split the families if that ever bites.
        if (appliedList.Count > 0 && appliedList.All(x => x.Tweak.TakesEffectAfterReboot))
            return KeptReport(appliedList, verdict: null);

        // Degraded mode: no baseline to compare against → cannot do an A/B verdict.
        // Keep the batch, no verdict; never invent a gain (honesty §5).
        if (baseline is null)
            return KeptReport(appliedList, verdict: null);

        Result<BenchmarkResult> after;
        try
        {
            after = await _bench.RunAsync(targetPid, ReBenchDuration, ReBenchRuns, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            after = Result<BenchmarkResult>.Fail(ErrorCodes.BenchmarkFailed, ex.Message);
        }

        // Cannot re-measure → keep the batch with a null verdict (don't claim a gain,
        // and don't auto-revert a change we have no measured reason to revert).
        if (!after.IsOk || after.Value is null)
            return KeptReport(appliedList, verdict: null);

        // La comparaison tient-elle, avant de s'en servir pour annuler ?
        //
        // Ce moteur auto-annule sur une regression mesuree — c'est sa garantie la plus forte. Mais
        // il le faisait sans jamais demander si les deux passes avaient mesure la meme session.
        // Le cas concret : le joueur alt-tabbe pendant la passe apres, l'hygiene lui retire 45 %
        // de ses trames, les FPS s'effondrent, et le moteur defait un reglage peut-etre bon a
        // cause d'une fenetre au premier plan. Rien ne l'aurait signale.
        //
        // Une experience qui ne tient pas se conclut comme une re-mesure impossible : on garde, et
        // on n'affirme rien. C'est la distinction que porte le modele de preuve — « je n'ai rien
        // vu » et « je n'ai pas mesure » ne se traitent pas pareil.
        if (AbComparer.Evidence(baseline, after.Value).Game?.Preuve.Outcome == EvidenceOutcome.Invalid)
            return KeptReport(appliedList, verdict: null);

        // Single source of truth for the significance rule (combined margins).
        var raw = AbComparer.Compare(baseline, after.Value);

        if (IsMeasuredRegression(raw))
        {
            // Auto-revert the WHOLE batch (LIFO) — we never keep a measured loss.
            var verdict = raw with { IsMeasurableGain = false, SummaryKey = SummaryRegressionReverted };
            return await AutoRevertBatchAsync(applied, verdict).ConfigureAwait(false);
        }

        // Un gain ne s'annonce que si la cible est prouvee en place.
        //
        // Et la regression, elle, se traite quand meme — c'est la distinction qui a failli
        // m'echapper. Refuser de MESURER faute de preuve supprimait aussi le filet : un batch qui
        // degrade la machine serait reste en place. Mesurer sert deux choses opposees, et une
        // seule exige une preuve d'application :
        //   * PROTEGER (annuler une regression) : aucune attribution, donc aucune preuve requise ;
        //   * AFFIRMER (annoncer un gain) : attribution a ce levier, donc preuve exigee.
        //
        // Sans cible prouvee, le batch est garde et l'ecart mesure n'est rapporte comme gain de
        // personne. « Je ne peux pas dire a qui l'attribuer » n'est pas « il n'y a pas de gain » :
        // le verdict est nul, pas negatif.
        if (raw.IsMeasurableGain)
            return cibleProuvee
                ? KeptReport(appliedList, raw with { SummaryKey = SummaryMeasurableGain })
                : KeptReport(appliedList, verdict: null);

        // No measurable gain: a VALID kept state (not a regression).
        return KeptReport(appliedList, raw with { IsMeasurableGain = false, SummaryKey = SummaryNoMeasurableGain });
    }

    // A MEASURED regression = "after" worse than "before" BEYOND combined margins.
    // AbComparer encodes this as not-a-gain AND its regression key; the regression
    // criterion there is PRIMARILY frame consistency (1% lows), with avg FPS as a
    // secondary trigger (latency is informative, not a trigger — plan §4 / C2). The
    // engine stays coupled to that single key so the verdict rule lives in one place.
    private static bool IsMeasuredRegression(AbVerdict raw) =>
        !raw.IsMeasurableGain && raw.SummaryKey == "Scan.Ab.Regression";

    private async Task<Result<ApplyReport>> AutoRevertBatchAsync(
        Stack<(ITweak Tweak, TweakSnapshot Snapshot)> applied, AbVerdict verdict)
    {
        var revertedIds = new HashSet<string>();
        var snapshotsInPushOrder = applied.Reverse().ToList();

        while (applied.Count > 0)
        {
            var (tweak, snapshot) = applied.Pop();   // LIFO
            Result revert;
            try
            {
                // The TWEAK's own revert (not _safety) so a COMPOSITE snapshot is fully
                // restored on a measured-regression auto-revert — same gap fix as above.
                revert = await tweak.RevertAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                revert = Result.Fail(ErrorCodes.HelperOpFailed, ex.Message);
            }

            if (revert.IsOk)
            {
                revertedIds.Add(tweak.Id);
                _diag.Change(tweak.Id, "applied", "auto-reverted", "DrDXT");
            }
            else
            {
                // Best-effort: the un-reverted id falls back to the System Restore point.
                _diag.Change(tweak.Id, "applied", "auto-revert-FAILED", "DrDXT");
            }
        }

        var outcomes = snapshotsInPushOrder
            .Select(x => new ApplyOutcome(
                x.Tweak.Id,
                Applied: true,
                AutoReverted: revertedIds.Contains(x.Tweak.Id),
                Snapshot: x.Snapshot,
                Error: null))
            .ToList();

        return Result<ApplyReport>.Ok(new ApplyReport(outcomes, verdict));
    }

    private static Result<ApplyReport> KeptReport(
        IReadOnlyList<(ITweak Tweak, TweakSnapshot Snapshot)> applied, AbVerdict? verdict)
    {
        var outcomes = applied
            .Select(x => new ApplyOutcome(
                x.Tweak.Id,
                Applied: true,
                AutoReverted: false,
                Snapshot: x.Snapshot,
                Error: null))
            .ToList();
        return Result<ApplyReport>.Ok(new ApplyReport(outcomes, verdict));
    }
}

/// <summary>Gating result of <see cref="TweakEngine.Plan"/> — the applicable tweaks
/// for this machine plus the ids skipped because they do not apply (master plan §16).
/// Kept local to the Application layer so Core §10's authoritative ITweakEngine shape
/// is untouched.</summary>
public sealed record EnginePlan(IReadOnlyList<ITweak> Applicable, IReadOnlyList<string> SkippedIds);
