using System.Diagnostics;
using DrDxt.Core;
using DrDxt.Measurement.PresentMon;

namespace DrDxt.Measurement;

/// <summary>
/// Orchestre N runs de capture PresentMon et agrège les frametimes en un <see cref="BenchmarkResult"/>.
///
/// Mode dégradé : si le service PresentMon est absent,
/// retourne <c>Fail(PmonUnavailable)</c> : le scanner peut alors mettre Baseline = null
/// et continuer à produire un rapport de scan sans mesure de performance.
/// Aucune exception ne remonte vers l'appelant, sauf l'annulation, qui reste une annulation.
/// </summary>
public sealed class BenchmarkRunner : IBenchmarkRunner
{
    private readonly IPresentMonSession _session;
    private readonly Func<int, bool> _isProcessAlive;
    // La pression de validation, injectable comme la liveness : un test la fixe sans toucher a la memoire de la machine.
    private readonly Func<double?> _commitPressure;

    /// <summary>Qui est au premier plan. Optionnel : sans sonde, le focus reste Unknown, et
    /// Unknown ne condamne aucune passe.</summary>
    private readonly IForegroundProbe? _foreground;
    private readonly IHardwareProbe? _materiel;

    public BenchmarkRunner(
        IPresentMonSession session,
        Func<int, bool>? isProcessAlive = null,
        Func<double?>? commitPressure = null,
        IForegroundProbe? foreground = null,
        IHardwareProbe? materiel = null)
    {
        _session = session;
        _isProcessAlive = isProcessAlive ?? DefaultProcessAlive;
        _commitPressure = commitPressure ?? CommitPressureReader.Read;
        _foreground = foreground;
        _materiel = materiel;
    }

    /// <summary>Le temps que la sonde a vu le jeu derrière une autre fenêtre pendant la passe, plus une seconde (elle lit
    /// toutes les 250 ms) : la limite des secondes que l'hygiène peut écarter comme hors focus. Au premier plan tout du long,
    /// ou sans lecture concluante, aucune : une chute lente et régulière vue par le joueur est la mesure, pas un alt-tab.</summary>
    internal static double SecondesHorsFocus(FocusObservation passe, IReadOnlyList<double> brut)
        => passe.FractionAuPremierPlan is { } f && f < 1
            ? Math.Ceiling((1 - f) * brut.Where(ms => ms > 0 && double.IsFinite(ms)).Sum() / 1000.0) + 1
            : 0;

    // Vraie vérification de vie : GetProcessById lève ArgumentException si le PID a disparu.
    private static bool DefaultProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false; // PID inconnu ou accès refusé → traité comme vie non prouvable
        }
    }

    /// <inheritdoc/>
    public async Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct)
    {
        if (runs < 1)
            return Result<BenchmarkResult>.Fail(ErrorCodes.CaptureFail,
                $"Run count must be >= 1 (got {runs}).");

        // Vérification de disponibilité du service avant de lancer les captures.
        var avail = _session.IsServiceAvailable();
        if (!avail.IsOk)
            return Result<BenchmarkResult>.Fail(avail.Error!.Value.Code, avail.Error.Value.Message);
        if (!avail.Value)
        {
            var why = _session.LastUnavailableDetail;
            return Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable,
                Docteur.T("BR.PresentMonIndisponible")
                + (string.IsNullOrWhiteSpace(why) ? string.Empty : Docteur.T("BR.Cause", why)));
        }

        var all = new List<double>();
        var perRunAvgFps = new List<double>();
        var perRunP1Low = new List<double>();
        var perRunP01Low = new List<double>();
        var allLatencies = new List<double>();
        var allDisplayLatencies = new List<double>();
        var allPcLatencies = new List<double>();
        var allPresentModes = new List<double>();
        var allGpuBusy = new List<double>();
        // Le denominateur de l'occupation GPU : les memes trames que `allGpuBusy`, et elles seules.
        var tramesAvecGpu = new List<double>();
        // Les quatre series du classifieur, accumulees en parallele de `all` et dans le meme ordre : les ratios apparient
        // l'index i de chacune, et une desynchronisation donnerait un chiffre faux sans erreur. MedianRatioOrNull refuse
        // de calculer quand les longueurs different.
        var allGpuWait = new List<double>();
        var allCpuBusy = new List<double>();
        var allPresentApi = new List<double>();
        var allAnimationError = new List<double>();
        // Le denominateur de l'exposition : les trames rendues avant hygiene. `retenuesVues / brutesVues` dit ce que la
        // mesure a garde (ou aurait garde quand le masque refuse de filtrer : la serie reste entiere).
        var brutesVues = 0;
        var retenuesVues = 0;
        // Le focus, accumule sur toutes les passes : la session entiere doit avoir ete jouee.
        var focus = FocusObservation.Aucune;
        var lecturesMateriel = new System.Collections.Concurrent.ConcurrentBag<HardwareReading>();

        // Rattraper une passe perdue : `InterRunMarginOfError` (`FrametimeStatistics`) vaut +∞ sous trois passes
        // utilisables, et le re-bench du moteur en demande trois ; une passe perdue, et plus rien ne se conclut, pas meme
        // une regression a annuler. Borne au nombre de passes demandees (boucler serait un defaut) ; aucun rattrapage
        // sous trois demandees, le choix de l'appelant (un smoke rapide).
        var rattrapagesRestants = runs >= 3 ? runs : 0;

        for (var i = 0; i < runs; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Echantillonne pendant la capture, jamais apres : le joueur est revenu au jeu entre-temps.
            using var arretFocus = new CancellationTokenSource();
            var focusPasse = FocusObservation.Aucune;
            var suiviFocus = _foreground is null
                ? Task.CompletedTask
                : Task.Run(async () =>
                {
                    while (!arretFocus.Token.IsCancellationRequested)
                    {
                        var lu = _foreground.ForegroundProcessId();
                        focus = focus.Avec(lu, targetPid);
                        focusPasse = focusPasse.Avec(lu, targetPid);
                        try { await Task.Delay(250, arretFocus.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }, CancellationToken.None);

            // La telemetrie materielle aussi : lue apres, elle decrit une carte qui redescend deja.
            var suiviMateriel = _materiel is null
                ? Task.CompletedTask
                : Task.Run(async () =>
                {
                    while (!arretFocus.Token.IsCancellationRequested)
                    {
                        try { lecturesMateriel.Add(_materiel.Lire()); } catch { /* jamais fatal */ }
                        try { await Task.Delay(250, arretFocus.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }, CancellationToken.None);

            var cap = await _session.CaptureAsync(targetPid, duration, ct).ConfigureAwait(false);
            arretFocus.Cancel();
            await suiviFocus.ConfigureAwait(false);
            await suiviMateriel.ConfigureAwait(false);
            // La session rend les images déjà vues quand le jeton tombe, ou un CaptureFail : ni l'une ni l'autre n'est
            // une passe, et l'annulation reste une annulation.
            ct.ThrowIfCancellationRequested();
            if (!cap.IsOk)
            {
                // Le jeu fermé ou la capture perdue en cours de route : les passes déjà réussies restent une mesure. Deux au
                // moins se gardent, même si la marge entre passes reste inconnue sous trois ; Runs dira combien ont servi.
                if (perRunAvgFps.Count >= 2)
                    break;

                // Service vivant, processus vivant, zero frame : soit rien n'est presente (menu, fenetre minimisee, onglet
                // en arriere-plan), soit l'anti-triche bloque la capture ETW. `NoPresentingFrames` decrit l'observation ;
                // `AntiCheatBlocked` affirmerait une cause (Firefox, sans anti-triche, rend aussi zero frame). Le message
                // nomme les deux hypotheses.
                if (cap.Error!.Value.Code == ErrorCodes.NoFrames && _isProcessAlive(targetPid))
                    return Result<BenchmarkResult>.Fail(ErrorCodes.NoPresentingFrames,
                        "Measure.NoPresentingFrames");

                return Result<BenchmarkResult>.Fail(cap.Error.Value.Code, cap.Error.Value.Message);
            }

            // Hygiene par run et sur les sept series : ici elles sont encore appariees par index (`FrameTimesMs` et six
            // annexes) ; filtrer la seule principale fausserait chaque ratio sans erreur. Le masque retire ce qui n'est
            // pas du jeu : le chargement, et les plafonds hors focus (30 fps derriere une autre fenetre), seulement le
            // temps ou la sonde a vu le jeu derrière une autre fenêtre. Un vrai ralentissement traverse (voir
            // `FrameSeriesHygiene.CapRegularityMax`), une chute V-Sync régulière au premier plan aussi.
            var brut = cap.Value!.FrameTimesMs;
            // En pleine partie (le relevé), aucun démarrage à couper : voir MesureEnPartie.
            var masque = FrameSeriesHygiene.Masque(brut, MesureEnPartie.Active ? 0 : FrameSeriesHygiene.DefaultWarmupSeconds,
                horsFocusMaxSecondes: SecondesHorsFocus(focusPasse, brut));
            brutesVues += brut.Count;
            retenuesVues += masque.Retenues;
            var frames = Filtrer(brut, masque);

            all.AddRange(frames);
            allLatencies.AddRange(Filtrer(cap.Value.RenderPresentLatencyMs, masque, brut.Count)!);
            // La latence d'affichage, même masque, même frame.
            if (cap.Value.DisplayLatencyMs is { } latenceAffichage)
                allDisplayLatencies.AddRange(Filtrer(latenceAffichage, masque, brut.Count)!);
            if (cap.Value.PcLatencyMs is { } latencePc)
                allPcLatencies.AddRange(Filtrer(latencePc, masque, brut.Count)!);
            // Les modes de présentation, même masque : comptés en double pour passer par le même filtre.
            if (cap.Value.PresentModes is { Count: > 0 } modes)
                AppendAligned(allPresentModes, Filtrer(modes.Select(m => (double)m).ToList(), masque, brut.Count), frames.Count);
            // La serie GPU emporte son propre denominateur, `tramesAvecGpu` : les trames d'une passe n'y entrent que si sa
            // serie GPU est entree au numerateur. Sinon une passe decalee sur cinq (quatre dans `allGpuBusy`, cinq dans
            // `all`) rendrait la part appariee incalculable et rallumerait le repli « moyenne sur moyenne ».
            var gpuBusy = Filtrer(cap.Value.GpuBusyMs, masque, brut.Count);
            if (gpuBusy is not null && gpuBusy.Count == frames.Count)
            {
                allGpuBusy.AddRange(gpuBusy);
                tramesAvecGpu.AddRange(frames);
            }
            AppendAligned(allGpuWait, Filtrer(cap.Value.GpuWaitMs, masque, brut.Count), frames.Count);
            AppendAligned(allCpuBusy, Filtrer(cap.Value.CpuBusyMs, masque, brut.Count), frames.Count);
            AppendAligned(allPresentApi, Filtrer(cap.Value.InPresentApiMs, masque, brut.Count), frames.Count);
            AppendAligned(allAnimationError, Filtrer(cap.Value.AnimationErrorMs, masque, brut.Count), frames.Count);

            // Agrégat par passe : la marge A/B est l'écart entre passes indépendantes, pas un intervalle par image (des
            // images autocorrélées le rendraient trop étroit). Une passe sans image utilisable n'entre pas dans la variance.
            var runAvg = FrametimeStatistics.AvgFps(frames);
            if (runAvg > 0)
            {
                perRunAvgFps.Add(runAvg);
                perRunP1Low.Add(FrametimeStatistics.P1LowFps(frames));
                perRunP01Low.Add(FrametimeStatistics.P01LowFps(frames));
            }
            else if (rattrapagesRestants > 0)
            {
                // Rien d'exploitable : la passe ne compte pas, on en refait une.
                rattrapagesRestants--;
                i--;
            }
        }

        // Des images sans temps exploitable (NaN, 0) ne font aucune passe : rendues Ok, elles feraient une mesure à 0 FPS
        // et le repli DXGI ne partirait pas.
        if (all.Count == 0 || perRunAvgFps.Count == 0)
            return Result<BenchmarkResult>.Fail(ErrorCodes.NoFrames,
                Docteur.T("BR.AucuneFrame"));

        // Ne pas filtrer ici : l'hygiene passe par run, plus haut ; recollees, les sept series n'ont plus de frontieres de
        // run. Mesure du 07/09/2026 : les 8 captures reelles de `tools/p3` gardent toutes leurs trames (0 seconde
        // retiree sur 2 170), toutes en pleine partie ; l'hygiene sert le lancement du jeu et l'alt-tab, absents des donnees.

        var result = new BenchmarkResult(
            AvgFps: FrametimeStatistics.AvgFps(all),
            P1LowFps: FrametimeStatistics.P1LowFps(all),
            P01LowFps: FrametimeStatistics.P01LowFps(all),
            P95FrametimeMs: FrametimeStatistics.PercentileOrNull(all, 0.95),
            FrameTimesMs: all,
            // Latence rendu-présentation (informative ; seulement la part présentation→affichage, pas la
            // latence système). Moyenne des relevés valides par image sur toutes les passes, null s'il n'y en a aucun.
            EstimatedPcLatencyMs: FrametimeStatistics.MeanOrNull(allLatencies),
            MarginOfError: FrametimeStatistics.InterRunMarginOfError(perRunAvgFps),
            P1LowMarginOfError: FrametimeStatistics.InterRunMarginOfError(perRunP1Low),
            P01LowMarginOfError: FrametimeStatistics.InterRunMarginOfError(perRunP01Low),
            // Les passes qui ont servi, pas celles demandées : un échec en cours de route en garde moins.
            Runs: perRunAvgFps.Count,
            // GPU busy (ms) : le temps de travail du GPU par image, en moyenne sur les passes. GpuBusy/FrameTime
            // distingue limité par le GPU (≈ temps d'image) et limité par le CPU (≪). Null sans relevé (non mesuré).
            MeanGpuBusyMs: FrametimeStatistics.MeanOrNull(allGpuBusy),
            // L'occupation GPU appariee, source de `GpuBusyPercent` : somme sur somme, sur les seules trames ou les deux
            // grandeurs existent. La moyenne au-dessus (le travail GPU d'une trame) ne sert pas a fabriquer ce pourcentage.
            GpuBusyShare: FrametimeStatistics.SumRatioOrNull(allGpuBusy, tramesAvecGpu),

            // Les grandeurs du classifieur : mediane pour les ratios (quelques frames aberrantes, un chargement, un alt-tab,
            // tireraient une moyenne), p95 pour les latences (un a-coup vit dans la queue). Null quand non mesuree : le
            // classifieur en tire UNKNOWN_PARTIAL, jamais un etat confiant.
            GpuWaitRatio: FrametimeStatistics.MedianRatioOrNull(allGpuWait, all),
            CpuBusyRatio: FrametimeStatistics.MedianRatioOrNull(allCpuBusy, all),
            PresentStallMsP95: FrametimeStatistics.PercentileOrNull(allPresentApi, 0.95),
            // Le compte, par le meme filtre : la valeur et sa garde portent sur la meme population.
            PresentStallSamples: FrametimeStatistics.EchantillonsExploitables(allPresentApi),
            RenderPresentLatencyMsP95: FrametimeStatistics.PercentileOrNull(allLatencies, 0.95),
            // La médiane, pas le p95 : un levier de latence se juge sur ce que le joueur sent à chaque image.
            DisplayLatencyMsMedian: FrametimeStatistics.PercentileOrNull(allDisplayLatencies, 0.5),
            // NaN par image quand le jeu ne pose pas les marqueurs : le filtre commun les retire, tout NaN = null.
            PcLatencyMsMedian: FrametimeStatistics.PercentileOrNull(allPcLatencies, 0.5),
            PcLatencyMsP95: FrametimeStatistics.PercentileOrNull(allPcLatencies, 0.95),
            PartImagesComposees: FrametimeStatistics.PartComposee(allPresentModes.Select(m => (int)m).ToList()),
            // Valeur absolue : l'erreur d'animation est signee, et 5 ms trop tot casse la cadence autant que 5 ms trop tard.
            AnimationErrorMsP95: FrametimeStatistics.AbsPercentileOrNull(allAnimationError, 0.95),
            // Le compte de ce p95, par le meme filtre : le nombre de trames ne dit pas combien d'echantillons d'animation
            // ont ete retenus.
            AnimationErrorSamples: FrametimeStatistics.EchantillonsAbsExploitables(allAnimationError),
            // La seule grandeur hors PresentMon : la fiabilite (gels, OOM), qu'aucun overclocking ne repare.
            CommitPressure: _commitPressure(),

            // L'exposition reelle, tous runs confondus : un alt-tab de vingt secondes d'un seul cote suffit a fabriquer un
            // gain. `AbComparer.Evidence` refuse de comparer deux expositions trop eloignees.
            HygieneKeptFraction: brutesVues > 0 ? (double)retenuesVues / brutesVues : null,

            // Le focus observe. Sans sonde, il reste null, et Unknown ne condamne pas.
            ForegroundFraction: focus.FractionAuPremierPlan,
            Materiel: HardwareObservation.De(lecturesMateriel.ToList()));

        return Result<BenchmarkResult>.Ok(result);
    }

    // Ajoute une serie seulement si elle a une entree par frame : une serie partielle decalerait tout ce qui suit, et les
    // ratios se calculeraient sur des paires de frames differentes, sans erreur.
    private static void AppendAligned(List<double> accumulator, IReadOnlyList<double>? series, int frameCount)
    {
        if (series is null || series.Count != frameCount) return;
        accumulator.AddRange(series);
    }

    /// <summary>Applique le masque d'hygiene a la serie principale.</summary>
    private static IReadOnlyList<double> Filtrer(
        IReadOnlyList<double> serie, FrameSeriesHygiene.MaskResult masque)
        => Filtrer(serie, masque, serie.Count)!;

    /// <summary>Applique le masque a une serie annexe. D'une autre longueur que la serie brute, elle est rendue telle
    /// quelle (un masque construit sur d'autres index inventerait un alignement), puis `AppendAligned` la rejette : une
    /// serie desynchronisee ne contribue a aucun ratio.</summary>
    private static IReadOnlyList<double>? Filtrer(
        IReadOnlyList<double>? serie, FrameSeriesHygiene.MaskResult masque, int longueurBrute)
    {
        if (serie is null) return null;
        if (!masque.Applied || serie.Count != longueurBrute) return serie;

        var gardees = new List<double>(serie.Count);
        for (var i = 0; i < serie.Count; i++)
            if (masque.Garde[i]) gardees.Add(serie[i]);
        return gardees;
    }
}
