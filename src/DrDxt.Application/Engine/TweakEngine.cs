using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Measurement;

namespace DrDxt.Application.Engine;

// -----------------------------------------------------------------------
// TweakEngine : le coeur transactionnel de la pose.
//
// Il orchestre et ne tient lui-meme aucune primitive privilegiee. Chaque ecriture
// privilegiee passe par les leviers (via IElevatedClient), et chaque retour arriere ou
// retrait automatique passe par le RevertAsync du levier lui-meme : un levier a snapshot
// composite (correctif X3D CCD, plafond VRR) est rendu en entier, pas seulement sa premiere
// op. Le moteur ne depend d'ISafetyManager que pour EnsureRestorePointAsync ;
// SafetyManager.RevertAsync reste la voie de retour d'un snapshot persiste sans instance de levier.
//
// Contrats : les formes canoniques de Core sont utilisees telles quelles :
//   TweakPlan(IReadOnlyList<string> TweakIds)
//   ApplyOutcome(string TweakId, bool Applied, bool AutoReverted, TweakSnapshot? Snapshot, Error? Error)
//   ApplyReport(IReadOnlyList<ApplyOutcome> Outcomes, AbVerdict? Verdict)
//   ITweakEngine.ApplyAsync(TweakPlan, BenchmarkResult?, int targetPid, CancellationToken)
// Les champs du lot s'en deduisent : leviers retires = Outcomes.Where(AutoReverted), phrase =
// Verdict.SummaryKey, snapshots poses = Outcomes.Where(Applied).Snapshot. Le tri « Plan(...) »
// vit sur le moteur concret (EnginePlan), hors de l'interface Core, pour ne pas toucher sa forme.
//
// Invariants :
//  - jamais un lot a moitie pose : au premier echec, tout ce qui est deja pose est rendu
//    dans l'ordre inverse ⇒ ENGINE.BATCH_REVERTED.
//  - rien n'est pose sans point de restauration verifie (EnsureRestorePointAsync).
//  - mesure honnete : seule une regression mesuree au-dela des marges combinees retire le
//    levier ; « aucun gain mesurable » est un etat garde valide ; un gain n'est annonce que
//    mesure. Pas de reference ou banc en echec ⇒ on garde, verdict nul (jamais de gain
//    invente, jamais de retrait sans raison mesuree).
//  - Result partout ; aucune exception ne sort (try/catch aux frontieres).
// -----------------------------------------------------------------------
/// <summary>
/// Pose un lot de leviers pour Optimiser : point de restauration verifie d'abord, puis chaque levier
/// pose et relu, et tout le lot rendu au premier echec. Apres la pose, une nouvelle mesure peut retirer
/// un levier, mais seulement sur une regression mesuree ; sans mesure, il garde et n'annonce aucun gain.
/// </summary>
public sealed class TweakEngine : ITweakEngine
{
    // Durée et nombre de passes de la remesure après application. Défauts prudents ;
    // les vraies valeurs dépendent de l'UX (barre de progression) : un seul réglage, ici.
    private static readonly TimeSpan ReBenchDuration = TimeSpan.FromSeconds(10);
    // ≥3 passes pour que la marge A/B entre passes soit finie et parlante : le retour automatique
    // est un verdict A/B, et sous 3 passes la marge est « non fiable » (∞) → aucune régression (ni
    // aucun gain) ne pourrait être prouvée, ce qui viderait le retour automatique mesuré de son sens.
    private const int ReBenchRuns = 3;

    private const string SummaryRegressionReverted = "verdict.regression_reverted";
    private const string SummaryRegressionRevertFailed = "verdict.regression_revert_failed";
    private const string SummaryNoMeasurableGain   = "verdict.no_measurable_gain";
    private const string SummaryMeasurableGain     = "verdict.measurable_gain";

    private readonly ISafetyManager _safety;
    private readonly IBenchmarkRunner _bench;
    private readonly IDiagnosticsLog _diag;
    private readonly ITweakCatalog _catalog;

    /// <summary>Le lecteur de registre qui sert a prouver qu'un levier est en place. Optionnel et
    /// en queue : sans lui, la verification rend <c>Unknown</c> et le moteur se comporte comme
    /// avant, mais alors il ne peut plus affirmer qu'un levier est applique.</summary>
    private readonly IRegistryReader? _registre;

    /// <summary>Le pilote NVIDIA, pour relire un reglage DRS applique. Optionnel : sans lui, un
    /// levier pilote rend `Unavailable`, donc aucun gain ne lui sera attribue.</summary>
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

    /// <summary>Filtrage : garde les leviers dont AppliesTo(hw,state) est vrai, et note les
    /// ids écartés parce qu'ils ne s'appliquent pas à cette machine. Pur, sans effet de bord :
    /// l'UI s'en sert pour dire « ce module ne s'applique pas à ta machine » au lieu de
    /// proposer l'impossible.</summary>
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
            // Garde de frontière : aucune exception ne sort jamais du moteur.
            return Result<ApplyReport>.Fail(ErrorCodes.BatchReverted, $"Engine boundary error: {ex.Message}");
        }
    }

    private async Task<Result<ApplyReport>> ApplyCoreAsync(
        TweakPlan plan, BenchmarkResult? baseline, int targetPid, CancellationToken ct)
    {
        // Ids du plan → leviers ; les ids absents du catalogue sont écartés proprement.
        var tweaks = new List<ITweak>();
        foreach (var id in plan.TweakIds)
        {
            var got = _catalog.Get(id);
            if (got.IsOk && got.Value is not null)
                tweaks.Add(got.Value);
        }

        // Rien à faire : pas même de point de restauration (pas de points en trop).
        if (tweaks.Count == 0)
            return Result<ApplyReport>.Ok(new ApplyReport(Array.Empty<ApplyOutcome>(), Verdict: null));

        // (1) Un point de restauration vérifié doit exister avant toute pose. Échec ⇒ abandon, rien de posé.
        var point = await _safety.EnsureRestorePointAsync().ConfigureAwait(false);
        if (!point.IsOk)
            return Result<ApplyReport>.Fail(point.Error!.Value.Code, point.Error!.Value.Message);

        // (2) Pose chaque levier et empile son snapshot (LIFO). Au premier échec
        //     ⇒ tout le lot est rendu (LIFO) ⇒ ENGINE.BATCH_REVERTED. Jamais à moitié.
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
            // Un `Result.Ok` de l'helper dit que l'op a ete envoyee, pas qu'elle a pris. Une
            // feuille protegee par une strategie de groupe, un pilote qui clampe, un autre outil
            // qui reecrit derriere : dans les trois cas l'apply « reussit » et la valeur cible
            // n'est pas la. Le moteur mesurait alors l'ecart d'une session et l'attribuait a un
            // changement inexistant : c'est ainsi qu'on fabrique un faux gain.
            //
            // Deux sorties, et elles ne se confondent pas.
            //
            // `NotApplied` et `Drifted` sont des echecs : dans le premier cas on sait que rien n'a
            // bouge, dans le second la feuille porte une troisieme valeur : quelqu'un d'autre
            // ecrit dessus. Les deux annulent le batch.
            //
            // `Unknown` et `Unavailable` (op non relisable, lecteur absent, snapshot ancien) ne
            // condamnent pas le levier : un plan d'alimentation ou une courbe de ventilation ne se
            // relisent pas dans le registre, et ils fonctionnent tres bien. Mais ils privent le
            // produit du droit d'attribuer une mesure a ce levier : le batch est donc garde et
            // l'A/B est saute, exactement comme pour un changement qui n'agit qu'apres redemarrage.
            if (_registre is not null)
            {
                var preuve = AppliedStateVerifier.Verifier(snapshot, _registre, _nvApi, _alimentation);
                _diag.Change(tweak.Id, preuve.RequestedState ?? "?",
                    $"readback:{preuve.Verdict}", "DrDXT");

                if (preuve.Verdict is EtatApplique.NotApplied or EtatApplique.Drifted)
                {
                    applied.Push((tweak, snapshot));   // pour que le rollback le defasse aussi
                    var lue = preuve.ObservedState ?? Docteur.T("TE.Absente");
                    var pourquoi = preuve.Verdict == EtatApplique.NotApplied
                        ? Docteur.T("TE.ResteeAvant", lue)
                        : Docteur.T("TE.AutreSource", lue);
                    return await RollbackBatchAsync(applied, tweak.Id,
                        new Error(ErrorCodes.TweakSdkUnavailable,
                            Docteur.T("TE.NonPris", tweak.Id, pourquoi, preuve.RequestedState)))
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
            // Journal d'audit : un changement fait par DrDXT (de = charge d'origine du snapshot, vers = posé).
            _diag.Change(tweak.Id, snapshot.OriginalValueJson, "applied", "DrDXT");
        }

        // (3) Tout est posé. Remesurer si possible ; retour automatique seulement sur une régression mesurée.
        return await EvaluateAndFinalizeAsync(applied, baseline, targetPid, toutProuve, ct).ConfigureAwait(false);
    }

    // --- Retour LIFO d'un lot qui échoue à la pose (ne lève jamais). ---
    private async Task<Result<ApplyReport>> RollbackBatchAsync(
        Stack<(ITweak Tweak, TweakSnapshot Snapshot)> applied, string failedId, Error? cause)
    {
        var unreverted = new List<string>();
        while (applied.Count > 0)
        {
            var (tweak, snapshot) = applied.Pop();   // LIFO : le dernier posé est rendu le premier
            Result revert;
            try
            {
                // Le retour arrière du levier lui-même (pas _safety), pour qu'un snapshot composite soit
                // rendu en entier : les leviers à une op délèguent au même décodage générique, rien ne
                // change pour eux, mais les retours X3D-CCD / plafond VRR rejouent tout leur composite.
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
                // Un retour arrière qui échoue lui-même pendant le rollback : journal CRITICAL et l'id est
                // gardé ; le dernier filet est le point de restauration. On ne lève pas.
                unreverted.Add(tweak.Id);
                _diag.Change(tweak.Id, "applied", "revert-FAILED", "DrDXT");
            }
        }

        // La cause survit au rollback. Ce message ne portait que l'identifiant et le code :
        // « apply of 'x' failed (TWEAK.SDK_UNAVAILABLE) ». Or c'est exactement ici que le produit
        // doit savoir pourquoi : feuille protegee, valeur clampee par le pilote, valeur lue restee
        // celle d'avant. Un diagnostic qui perd sa cause au moment de l'annuler ne vaut pas mieux
        // qu'une erreur generique.
        var detail = $"apply of '{failedId}' failed ({cause?.Code})";
        if (!string.IsNullOrWhiteSpace(cause?.Message))
            detail += $": {cause?.Message}";
        if (unreverted.Count > 0)
            detail += $"; rollback could not revert: {string.Join(", ", unreverted)} (use the System Restore point)";

        return Result<ApplyReport>.Fail(ErrorCodes.BatchReverted, detail);
    }

    // --- Remesure → significativité AbComparer → on garde ou retour automatique. ---
    private async Task<Result<ApplyReport>> EvaluateAndFinalizeAsync(
        Stack<(ITweak Tweak, TweakSnapshot Snapshot)> applied,
        BenchmarkResult? baseline, int targetPid, bool cibleProuvee, CancellationToken ct)
    {
        // Dans l'ordre de pose (le plus ancien d'abord), pour un rapport stable et lisible.
        var appliedList = applied.Reverse().ToList();

        // Lot qui exige un redémarrage (HAGS, Game Mode, …) : le changement n'agit qu'au prochain
        // démarrage ou lancement du jeu, une remesure immédiate ne peut pas le refléter, et le bruit
        // de session ne doit jamais annuler un changement dont l'effet n'est même pas actif. Quand tout
        // le lot exige un redémarrage, on saute l'A/B et on garde ; le gain se prouve au scan en jeu
        // suivant (preuve différée, honnête). heuristique : un lot mixte est quand même remesuré
        // (les leviers immédiats le sont) ; séparer les familles si ça gêne un jour.
        if (appliedList.Count > 0 && appliedList.All(x => x.Tweak.TakesEffectAfterReboot))
            return KeptReport(appliedList, verdict: null);

        // Mode dégradé : pas de base de comparaison → pas de verdict A/B possible.
        // On garde le lot, sans verdict ; jamais de gain inventé.
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

        // Remesure impossible → on garde le lot avec un verdict nul (aucun gain affirmé, et
        // aucun retour automatique d'un changement qu'aucune mesure ne justifie d'annuler).
        if (!after.IsOk || after.Value is null)
            return KeptReport(appliedList, verdict: null);

        // La comparaison tient-elle, avant de s'en servir pour annuler ?
        //
        // Ce moteur auto-annule sur une regression mesuree : c'est sa garantie la plus forte. Mais
        // il le faisait sans jamais demander si les deux passes avaient mesure la meme session.
        // Le cas concret : le joueur alt-tabbe pendant la passe apres, l'hygiene lui retire 45 %
        // de ses trames, les FPS s'effondrent, et le moteur defait un reglage peut-etre bon a
        // cause d'une fenetre au premier plan. Rien ne l'aurait signale.
        //
        // Une experience qui ne tient pas se conclut comme une re-mesure impossible : on garde, et
        // on n'affirme rien. C'est la distinction que porte le modele de preuve : « je n'ai rien
        // vu » et « je n'ai pas mesure » ne se traitent pas pareil.
        if (AbComparer.Evidence(baseline, after.Value).Game?.Preuve.Outcome == EvidenceOutcome.Invalid)
            return KeptReport(appliedList, verdict: null);

        // Source unique de la règle de significativité (marges cumulées).
        var raw = AbComparer.Compare(baseline, after.Value);

        if (IsMeasuredRegression(raw))
        {
            // Retour automatique de tout le lot (LIFO) : on ne garde jamais une perte mesurée.
            var verdict = raw with { IsMeasurableGain = false, SummaryKey = SummaryRegressionReverted };
            return await AutoRevertBatchAsync(applied, verdict).ConfigureAwait(false);
        }

        // Un gain ne s'annonce que si la cible est prouvee en place.
        //
        // Et la regression, elle, se traite quand meme : c'est la distinction qui compte.
        // Refuser de mesurer faute de preuve supprimerait aussi le filet : un batch qui
        // degrade la machine resterait en place. Mesurer sert deux choses opposees, et une
        // seule exige une preuve d'application :
        //   * proteger (annuler une regression) : aucune attribution, donc aucune preuve requise ;
        //   * affirmer (annoncer un gain) : attribution a ce levier, donc preuve exigee.
        //
        // Sans cible prouvee, le batch est garde et l'ecart mesure n'est rapporte comme gain de
        // personne. « Je ne peux pas dire a qui l'attribuer » n'est pas « il n'y a pas de gain » :
        // le verdict est nul, pas negatif.
        if (raw.IsMeasurableGain)
            return cibleProuvee
                ? KeptReport(appliedList, raw with { SummaryKey = SummaryMeasurableGain })
                : KeptReport(appliedList, verdict: null);

        // Aucun gain mesurable : un état gardé valide (pas une régression).
        return KeptReport(appliedList, raw with { IsMeasurableGain = false, SummaryKey = SummaryNoMeasurableGain });
    }

    // Une régression mesurée = « après » pire qu'« avant » au-delà des marges cumulées.
    // AbComparer l'encode comme « pas un gain » et sa clé de régression ; son critère est
    // d'abord la régularité des trames (1 % lows), le FPS moyen servant de déclencheur
    // secondaire (la latence informe, elle ne déclenche pas). Le moteur reste lié
    // à cette seule clé, pour que la règle du verdict vive à un seul endroit.
    private static bool IsMeasuredRegression(AbVerdict raw) =>
        !raw.IsMeasurableGain && raw.SummaryKey == "Scan.Ab.Regression";

    private async Task<Result<ApplyReport>> AutoRevertBatchAsync(
        Stack<(ITweak Tweak, TweakSnapshot Snapshot)> applied, AbVerdict verdict)
    {
        var revertedIds = new HashSet<string>();
        var echecs = new Dictionary<string, Error>();
        var snapshotsInPushOrder = applied.Reverse().ToList();

        while (applied.Count > 0)
        {
            var (tweak, snapshot) = applied.Pop();   // LIFO
            Result revert;
            try
            {
                // Le retour arrière du levier lui-même (pas _safety), pour qu'un snapshot composite soit
                // rendu en entier lors d'un retour automatique sur régression mesurée, comme plus haut.
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
                // Au mieux : le levier non rendu retombe sur le point de restauration. L'erreur part aussi dans le
                // rapport : sans elle, l'écran dirait « changements annulés » sur un levier toujours posé.
                _diag.Change(tweak.Id, "applied", "auto-revert-FAILED", "DrDXT");
                echecs[tweak.Id] = revert.Error ?? new Error(ErrorCodes.HelperOpFailed, "auto-revert failed");
            }
        }

        var outcomes = snapshotsInPushOrder
            .Select(x => new ApplyOutcome(
                x.Tweak.Id,
                Applied: true,
                AutoReverted: revertedIds.Contains(x.Tweak.Id),
                Snapshot: x.Snapshot,
                Error: echecs.GetValueOrDefault(x.Tweak.Id)))
            .ToList();

        if (echecs.Count > 0) verdict = verdict with { SummaryKey = SummaryRegressionRevertFailed };
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

/// <summary>Résultat du filtrage de <see cref="TweakEngine.Plan"/> : les leviers applicables
/// à cette machine et les ids écartés parce qu'ils ne s'appliquent pas.
/// Gardé dans la couche Application pour ne pas toucher la forme d'ITweakEngine, qui fait
/// foi dans Core.</summary>
public sealed record EnginePlan(IReadOnlyList<ITweak> Applicable, IReadOnlyList<string> SkippedIds);
