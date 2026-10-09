using DrDxt.Core;

namespace DrDxt.Detection.Scoring;

/// <summary>
/// Répond à « qu'est-ce qui te coûte des images ? » — et donc à son inverse, « qu'est-ce qu'il
/// ne faut pas te vendre parce que ça ne changera rien chez toi ».
///
/// Pur et total : aucune E/S, aucune horloge, aucun état. Même entrée, même verdict.
///
/// L'ordre des tests est la politique, pas un détail d'écriture. Il va du plus grave et du
/// moins réparable-par-un-réglage vers le plus fin :
///   1. rien de mesurable        → on ne dit rien (UNKNOWN_PARTIAL) ;
///   2. la mémoire étrangle      → une machine qui gèle ne se répare pas en overclockant ;
///   3. un cap verrouille tout   → la mesure ne peut rien prouver, aucun A/B n'est valide ;
///   4. le GPU est bridé         → ajouter de la puissance à un GPU déjà chaud est une bêtise ;
///   5. la cadence est cassée    → 140 FPS de moyenne peuvent être désagréables à jouer ;
///   6. qui pace la frame        → GPU / mixte / CPU, la question classique ;
///   7. tout va bien mais lent   → il ne reste que la latence.
///
/// Inverser 2 et 6 suffirait à vendre un overclock GPU à une machine qui manque de mémoire de
/// validation. C'est la raison de cet ordre, et il est verrouillé par des tests.
///
/// Source des seuils : la note de conception du classifieur (non publiée). Les grandeurs
/// viennent de PresentMon (github.com/GameTechDev/PresentMon, README-ConsoleApplication.md). La spec
/// dit elle-même qu'aucun seuil canonique public n'existe : ce sont des seuils de POLITIQUE
/// DrDXT, dérivés de la sémantique des métriques PresentMon. Chacun est un bouton de calibration.
/// </summary>
public static class BottleneckClassifier
{
    // ── Seuils de politique. Chaque nom porte la ligne de la table de décision qu'il sert. ──

    /// <summary>`gpu_busy_ratio >= 0.92` ⇒ le GPU pace quasiment toute la frame.</summary>
    public const double GpuBoundBusy = 0.92;

    /// <summary>`gpu_wait_ratio <= 0.08` accompagne GPU_BOUND : le GPU n'attend presque pas.</summary>
    public const double GpuBoundWait = 0.08;

    /// <summary>Plancher de la bande mixte (0,80..0,92).</summary>
    public const double MixedGpuLeaningBusy = 0.80;

    /// <summary>`gpu_wait_ratio <= 0.15` accompagne la bande mixte.</summary>
    public const double MixedGpuLeaningWait = 0.15;

    /// <summary>`gpu_busy_ratio < 0.75` + attente élevée ⇒ CPU/app donne le tempo.</summary>
    public const double CpuBoundBusy = 0.75;

    /// <summary>`gpu_wait_ratio >= 0.20` : le GPU poireaute une part notable de la frame.</summary>
    public const double CpuBoundWait = 0.20;

    /// <summary>`gpu_busy_ratio < 0.60` ⇒ franchement CPU/app.</summary>
    public const double CpuBoundStrongBusy = 0.60;

    /// <summary>Validation mémoire ≥ 90 % de la limite. Le seul seuil de cette table qui soit
    /// sourcé chez l'éditeur : Microsoft documente 90 % comme déclencheur de croissance du
    /// fichier d'échange géré par le système.</summary>
    public const double CommitPressureLimit = 0.90;

    /// <summary>`frame_ms_cv >= 0.12` ⇒ cadence irrégulière.</summary>
    public const double FramePacingCv = 0.12;

    /// <summary>`p99 / p50 >= 1.75` ⇒ à-coups rares mais francs, que le CV peut manquer.</summary>
    public const double FramePacingP99OverP50 = 1.75;

    /// <summary>`animation_error_p95 >= 4 ms` ⇒ cadence d'animation cassée.</summary>
    public const double FramePacingAnimationErrorMs = 4.0;

    /// <summary>Sous un cap : le GPU n'est pas saturé et on attend dans Present().</summary>
    public const double CappedBusyCeiling = 0.90;

    /// <summary>Attente dans Present() qui trahit un cap / une vsync. heuristic-v1 : la spec
    /// nomme la condition (« MsInPresentAPI indicates waiting ») sans publier de valeur ;
    /// 2 ms est le choix conservateur de DrDXT. Bouton de calibration.</summary>
    public const double CappedPresentStallMs = 2.0;

    /// <summary>Multiple du frametime MEDIAN au-delà duquel la latence render→present p95 est
    /// dite haute. heuristic-v1 : la spec nomme la condition (« MsRenderPresentLatency high »)
    /// sans publier de valeur. 2× le frametime médian = la frame met plus de deux frames à
    /// sortir, ce qui se sent. Exprimé en RELATION et pas en millisecondes fixes, sans quoi le
    /// seuil serait juste à 143 FPS et faux à 60. Bouton de calibration.</summary>
    public const double LatencyHighFactor = 2.0;

    /// <summary>Nombre de runs qu'exige une affirmation forte. Spec : « Strong claims require
    /// 3-run repeatability » — en dessous, le verdict s'affiche mais reste provisoire.</summary>
    public const int RunsForStrongClaim = 3;

    /// <summary>
    /// Classe une session mesurée.
    /// </summary>
    /// <param name="result">
    /// La mesure. <c>null</c> (scan sans jeu, service indisponible) ⇒ UNKNOWN_PARTIAL, jamais un
    /// état confiant par défaut.
    /// </param>
    /// <param name="frameCap">
    /// Le verdict de cap déjà prononcé ailleurs par le produit, quand il existe. Un cap CONNU
    /// suffit à classer CAPPED sans attendre que les grandeurs de frame le trahissent — et une
    /// session bridée est celle où l'A/B ment le plus.
    /// </param>
    /// <param name="gpuThrottling">
    /// Vrai quand la télémétrie constructeur dit que le GPU est bridé (température / puissance).
    /// <c>null</c> = pas lisible, et on ne le devine pas : la spec range explicitement le
    /// thermique dans ce qui « needs vendor telemetry ».
    /// </param>
    public static BottleneckVerdict Classify(
        BenchmarkResult? result,
        FrameCapState? frameCap = null,
        bool? gpuThrottling = null)
    {
        if (result is null)
            return Unknown("No measurement was taken, so nothing is claimed about what limits this machine.",
                BottleneckReasonKeys.NoMeasurement);

        var provisional = result.Runs < RunsForStrongClaim;

        // ── 1. Rien de mesurable ────────────────────────────────────────────────────────────
        // Le ratio GPU-busy est la grandeur pivot : sans lui, six des dix états sont hors de
        // portée. On le dit, au lieu de retomber sur un état qui aurait l'air d'un diagnostic.
        if (result.GpuBusyRatio is not { } gpuBusy)
        {
            // Une seule exception : la pression mémoire ne dépend d'aucune grandeur de frame.
            // Une machine qui gèle par manque de validation mérite de l'apprendre même quand la
            // capture a échoué — c'est justement ce que fait une machine qui gèle.
            if (result.CommitPressure is { } onlyCommit && onlyCommit >= CommitPressureLimit)
                return CommitVerdict(onlyCommit, provisional);

            return Unknown(
                "GPU busy was not measured (anti-cheat, missing GPU tracking, or too few frames), "
                + "so no limiter is claimed and only config-safe items are proposed.",
                BottleneckReasonKeys.GpuBusyUnmeasured);
        }

        // ── 2. La mémoire étrangle ──────────────────────────────────────────────────────────
        // Avant tout le reste, et pas par excès de prudence : sur une machine à la limite de
        // validation, Microsoft documente des gels et des plantages. Classer d'abord « GPU-bound »
        // ferait proposer un overclock à quelqu'un dont la machine se fige.
        if (result.CommitPressure is { } commit && commit >= CommitPressureLimit)
            return CommitVerdict(commit, provisional);

        // ── 3. Un cap verrouille la cadence ─────────────────────────────────────────────────
        // Le plus important pour l'honnêteté du produit : sous un cap, la moyenne FPS ne bouge
        // pas quoi qu'on règle, donc tout A/B sur la moyenne est une illusion. C'est exactement
        // le piège mesuré le 01/09 (ne rien faire « gagnait » plus que l'overclock).
        if (IsCapped(frameCap, gpuBusy, result.PresentStallMsP95, out var capReason))
            return new BottleneckVerdict(
                BottleneckState.CappedOrVsyncLimited,
                BottleneckConfidence.High,
                capReason,
                provisional,
                BottleneckReasonKeys.Capped);

        // ── 4. Le GPU est bridé ─────────────────────────────────────────────────────────────
        if (gpuThrottling == true)
            return new BottleneckVerdict(
                BottleneckState.ThermalOrPowerThrottle,
                BottleneckConfidence.Medium,
                "The GPU reports a temperature or power limit, so it is already giving what it can. "
                + "More power or clock is not the answer until it has thermal headroom.",
                provisional,
                BottleneckReasonKeys.Throttling);

        // ── 5. La cadence est cassée ────────────────────────────────────────────────────────
        // Avant la question « qui pace la frame », parce qu'une moyenne de 140 FPS peut être
        // désagréable à jouer, et que c'est ça que le joueur ressent.
        if (IsFramePacing(result, out var pacingReason))
            return new BottleneckVerdict(
                BottleneckState.FramePacing,
                BottleneckConfidence.Medium,
                pacingReason,
                provisional,
                BottleneckReasonKeys.FramePacing);

        // ── 6. Qui pace la frame ────────────────────────────────────────────────────────────
        var gpuWait = result.GpuWaitRatio;

        if (gpuBusy >= GpuBoundBusy && WaitAtMost(gpuWait, GpuBoundWait))
            return new BottleneckVerdict(
                BottleneckState.GpuBound,
                provisional ? BottleneckConfidence.Medium : BottleneckConfidence.High,
                $"The GPU paced {gpuBusy:P0} of each frame and barely waited, so the GPU is the limit. "
                + "CPU-side claims and RAM frequency claims are suppressed for this machine.",
                provisional,
                BottleneckReasonKeys.GpuBound, new[] { gpuBusy });

        if (gpuBusy < CpuBoundStrongBusy && WaitAtLeast(gpuWait, CpuBoundWait))
            return new BottleneckVerdict(
                BottleneckState.CpuBoundStrong,
                provisional ? BottleneckConfidence.Medium : BottleneckConfidence.High,
                $"The GPU paced only {gpuBusy:P0} of each frame and spent the rest waiting, so the CPU "
                + "or the game itself sets the pace. GPU power and clock changes are not an FPS answer here.",
                provisional,
                BottleneckReasonKeys.CpuBoundStrong, new[] { gpuBusy });

        if (gpuBusy < CpuBoundBusy && WaitAtLeast(gpuWait, CpuBoundWait))
            return new BottleneckVerdict(
                BottleneckState.CpuBoundOrAppBound,
                BottleneckConfidence.Medium,
                $"The GPU paced {gpuBusy:P0} of each frame while waiting for work, which points at the "
                + "CPU or the game rather than the graphics card.",
                provisional,
                BottleneckReasonKeys.CpuOrAppBound, new[] { gpuBusy });

        if (gpuBusy >= MixedGpuLeaningBusy && WaitAtMost(gpuWait, MixedGpuLeaningWait))
            return new BottleneckVerdict(
                BottleneckState.MixedGpuLeaning,
                BottleneckConfidence.Medium,
                $"The GPU paced {gpuBusy:P0} of each frame, mostly the limit, but not enough to say so "
                + "outright. GPU changes MAY help and must be proven by an A/B before they are kept.",
                provisional,
                BottleneckReasonKeys.MixedGpuLeaning, new[] { gpuBusy });

        // ── 7. Le débit va, la latence non ──────────────────────────────────────────────────
        // La latence « haute » est une RELATION, jamais un nombre magique : 14 ms est mauvais à
        // 143 FPS et parfaitement normal à 60. Un seuil en millisecondes fixes serait juste sur
        // une machine et faux sur toutes les autres. On la compare donc au frametime médian.
        //
        // Premier jet de ce classifieur : la condition ne testait que « la latence a été
        // mesurée », sans jamais demander qu'elle soit MAUVAISE. Deux tests l'ont attrapé —
        // des sessions parfaitement saines ressortaient « problème de latence ».
        if (result.RenderPresentLatencyMsP95 is { } latencyP95
            && result.FrameMsP50 is { } medianFrameMs and > 0
            && latencyP95 >= LatencyHighFactor * medianFrameMs
            && result.FrameMsCv is { } cv && cv < FramePacingCv)
            return new BottleneckVerdict(
                BottleneckState.LatencyOnly,
                BottleneckConfidence.Medium,
                $"Frame rate and pacing are fine, but render-present latency reaches {latencyP95:F1} ms at p95 "
                + $"for a {medianFrameMs:F1} ms median frame. What is left to win here is responsiveness, not frames.",
                provisional,
                BottleneckReasonKeys.LatencyOnly, new[] { latencyP95, medianFrameMs });

        // ── Rien ne tranche ─────────────────────────────────────────────────────────────────
        // La bande 0,75–0,80, ou une attente GPU non mesurée. Le dire est le seul geste honnête :
        // choisir l'état voisin « qui va bien » est exactement ce que ce classifieur existe pour
        // ne plus faire.
        return Unknown(
            $"The GPU paced {gpuBusy:P0} of each frame, which falls between the states this "
            + "classifier is willing to name. No limiter is claimed.",
            BottleneckReasonKeys.BetweenStates, gpuBusy);
    }

    // ── Les états qui ont leur propre logique ───────────────────────────────────────────────

    private static BottleneckVerdict CommitVerdict(double commit, bool provisional) =>
        new(BottleneckState.RamCommitPressure,
            // Le seul seuil sourcé chez l'éditeur de la table → la confiance est haute même en
            // un seul run : ce n'est pas une mesure de performance, c'est un état système.
            BottleneckConfidence.High,
            $"Committed memory is at {commit:P0} of the system limit. Microsoft documents that reaching "
            + "the commit limit can freeze or crash processes. This is a RELIABILITY problem, not a "
            + "frame-rate one: no FPS gain is claimed from fixing it.",
            provisional,
            BottleneckReasonKeys.CommitPressure, new[] { commit });

    private static bool IsCapped(
        FrameCapState? frameCap, double gpuBusy, double? presentStallMsP95, out string reason)
    {
        // Un cap déjà PRONONCÉ par le produit vaut mieux que son inférence depuis les frames :
        // c'est une lecture du pilote, pas une déduction.
        //
        // On ne retient que les deux verdicts qui disent d'eux-mêmes que le cap MORD
        // (« costs frames », « binds under the primary refresh »). `Coherent` est délibérément
        // absent : un cap correct à refresh−3 existe mais ne bride que si le jeu l'atteint, et
        // rien ici ne dit à quel FPS il tourne. S'il mord vraiment, la signature de frame
        // ci-dessous l'attrape — c'est exactement à ça qu'elle sert. Le classer capé sur le seul
        // verdict `Coherent` interdirait tout A/B à une machine bien réglée qui n'atteint jamais
        // son cap.
        if (frameCap is { Verdict: FrameCapVerdict.ComputedOnAnotherDisplay or FrameCapVerdict.CapsBelowPrimary })
        {
            reason = "A frame cap is active on this machine, so average FPS cannot move whatever is "
                   + $"changed: {frameCap.Value.Reason} No measured-gain claim is valid while it holds.";
            return true;
        }

        // Sinon, la signature : le GPU n'est pas saturé et on attend dans Present().
        if (gpuBusy < CappedBusyCeiling && presentStallMsP95 is { } stall && stall >= CappedPresentStallMs)
        {
            reason = $"The GPU paced only {gpuBusy:P0} of each frame while {stall:F1} ms at p95 was spent "
                   + "waiting inside Present(): the signature of a cap, vsync or compositor limit. "
                   + "An A/B on average FPS would measure the cap, not the change.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsFramePacing(BenchmarkResult result, out string reason)
    {
        if (result.FrameMsCv is { } cv && cv >= FramePacingCv)
        {
            reason = $"Frame times vary by {cv:P0} of their mean, so the pacing is uneven even if the "
                   + "average looks healthy. That unevenness is what is actually felt.";
            return true;
        }

        if (result.FrameMsP99OverP50 is { } spike && spike >= FramePacingP99OverP50)
        {
            reason = $"The worst 1% of frames take {spike:F1}× the median, so rare but hard hitches "
                   + "punctuate an otherwise fine average.";
            return true;
        }

        if (result.AnimationErrorMsP95 is { } animation && animation >= FramePacingAnimationErrorMs)
        {
            reason = $"Animation error reaches {animation:F1} ms at p95, so frames arrive off-beat from "
                   + "the simulation. The average frame rate does not describe this.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    // ── Les gardes d'attente GPU ────────────────────────────────────────────────────────────
    // Une attente NON mesurée ne doit ni confirmer ni infirmer. Ces deux fonctions disent
    // explicitement ce qu'il advient d'un null, au lieu de le laisser à un `?? 0` qui aurait
    // silencieusement rendu tout le monde GPU-bound.

    /// <summary>L'attente est-elle au plus <paramref name="ceiling"/> ? Une attente non mesurée
    /// PASSE : GPU_BOUND est alors décidé par le seul ratio busy, ce qui est le comportement
    /// d'avant ce classifieur — dégradé, pas faux.</summary>
    private static bool WaitAtMost(double? wait, double ceiling) => wait is null || wait <= ceiling;

    /// <summary>L'attente est-elle au moins <paramref name="floor"/> ? Une attente non mesurée
    /// Échoue : accuser le CPU demande la preuve que le GPU attendait vraiment, et cette preuve
    /// est précisément cette grandeur.</summary>
    private static bool WaitAtLeast(double? wait, double floor) => wait is { } w && w >= floor;

    private static BottleneckVerdict Unknown(
        string reason, string reasonKey, params double[] values) =>
        new(BottleneckState.UnknownPartial,
            // Confiance haute dans l'aveu lui-même : « je ne sais pas » est une réponse sûre,
            // et la spec la classe explicitement « High honesty ».
            BottleneckConfidence.High,
            reason,
            Provisional: true,
            ReasonKey: reasonKey,
            ReasonValues: values);
}
