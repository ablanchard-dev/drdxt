using System.Diagnostics;
using DrDxt.Core;
using DrDxt.Measurement.PresentMon;

namespace DrDxt.Measurement;

/// <summary>
/// Orchestre N runs de capture PresentMon et agrège les frametimes en un <see cref="BenchmarkResult"/>.
///
/// Mode dégradé (CRITIQUE M1) : si le service PresentMon est absent,
/// retourne <c>Fail(PmonUnavailable)</c> — le scanner peut alors mettre Baseline = null
/// et continuer à produire un rapport de scan sans mesure de performance.
/// Aucune exception ne remonte vers l'appelant.
/// </summary>
public sealed class BenchmarkRunner : IBenchmarkRunner
{
    private readonly IPresentMonSession _session;
    private readonly Func<int, bool> _isProcessAlive;
    // L1.1 — la pression de validation, en couture injectable comme la liveness : un test doit
    // pouvoir la fixer sans toucher a la memoire de la machine qui fait tourner la suite.
    private readonly Func<double?> _commitPressure;

    /// <summary>Qui est au premier plan. Optionnel : sans sonde, le focus reste UNKNOWN, et
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

    // Real liveness check: GetProcessById throws ArgumentException if the PID is gone.
    private static bool DefaultProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false; // unknown PID or access denied → treat as not provably alive
        }
    }

    /// <inheritdoc/>
    public async Task<Result<BenchmarkResult>> RunAsync(int targetPid, TimeSpan duration, int runs, CancellationToken ct)
    {
        if (runs < 1)
            return Result<BenchmarkResult>.Fail(ErrorCodes.CaptureFail,
                $"Le nombre de runs doit être >= 1 (reçu : {runs}).");

        // Vérification de disponibilité du service avant de lancer les captures.
        var avail = _session.IsServiceAvailable();
        if (!avail.IsOk)
            return Result<BenchmarkResult>.Fail(avail.Error!.Value.Code, avail.Error.Value.Message);
        if (!avail.Value)
        {
            var why = _session.LastUnavailableDetail;
            return Result<BenchmarkResult>.Fail(ErrorCodes.PmonUnavailable,
                "Service PresentMon indisponible: mode dégradé (Baseline = null)."
                + (string.IsNullOrWhiteSpace(why) ? string.Empty : $" Cause : {why}"));
        }

        var all = new List<double>();
        var perRunAvgFps = new List<double>();
        var perRunP1Low = new List<double>();
        var perRunP01Low = new List<double>();
        var allLatencies = new List<double>();
        var allGpuBusy = new List<double>();
        // Le denominateur de l'occupation GPU : les memes trames que `allGpuBusy`, et elles seules.
        var tramesAvecGpu = new List<double>();
        // L1.1 — les quatre series du classifieur. Elles sont accumulees EN PARALLELE de `all`
        // et dans le meme ordre : les ratios par frame en aval apparient l'index i de l'une
        // avec l'index i de l'autre, donc une desynchronisation ne se verrait pas, elle
        // produirait juste un chiffre faux. MedianRatioOrNull refuse d'ailleurs de calculer
        // quand les longueurs different.
        var allGpuWait = new List<double>();
        var allCpuBusy = new List<double>();
        var allPresentApi = new List<double>();
        var allAnimationError = new List<double>();
        // Le denominateur de l'exposition : combien de trames les captures ont RENDUES,
        // avant hygiene. `all.Count / brutesVues` dit ce que la mesure a reellement garde.
        var brutesVues = 0;
        // Le focus, accumule sur toutes les passes : c'est la session entiere qui doit avoir ete
        // jouee, pas seulement sa premiere minute.
        var focus = FocusObservation.Aucune;
        var lecturesMateriel = new System.Collections.Concurrent.ConcurrentBag<HardwareReading>();

        // Rattraper une passe perdue, parce qu'une seule suffit a tuer la resolution.
        //
        // `InterRunMarginOfError` vaut +∞ sous trois passes utilisables, et une passe qui ne rend
        // aucune trame exploitable n'en est pas une. Le re-bench du moteur en demande exactement
        // trois : une passe perdue, et le produit ne peut plus rien conclure -- y compris une
        // regression qu'il aurait fallu annuler. Ce n'est pas une hypothese, c'est l'arithmetique
        // de `FrametimeStatistics`.
        //
        // Le rattrapage est borne au nombre de passes demandees. Sur une machine ou la capture ne
        // marche pas, le banc rend ce qu'il a avec sa resolution inconnue et assumee : refaire une
        // passe est une correction, boucler serait un defaut.
        //
        // Aucun rattrapage sous trois passes demandees : demander une passe est un choix de
        // l'appelant (un smoke rapide), et en faire trois a sa place rendrait une mesure que
        // personne n'a demandee.
        var rattrapagesRestants = runs >= 3 ? runs : 0;

        for (var i = 0; i < runs; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Echantillonne pendant la capture, jamais apres. Le meme piege que la telemetrie
            // GPU : lire le focus une fois la passe finie ne dit rien de ce qui s'est passe
            // pendant — le joueur est revenu au jeu entre-temps.
            using var arretFocus = new CancellationTokenSource();
            var suiviFocus = _foreground is null
                ? Task.CompletedTask
                : Task.Run(async () =>
                {
                    while (!arretFocus.Token.IsCancellationRequested)
                    {
                        focus = focus.Avec(_foreground.ForegroundProcessId(), targetPid);
                        try { await Task.Delay(250, arretFocus.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }, CancellationToken.None);

            // Meme regle, meme boucle : la telemetrie materielle se lit pendant la passe. Lue
            // apres, elle decrit une carte qui a deja commence a redescendre.
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
            if (!cap.IsOk)
            {
                // Le service repond (verifie plus haut), le processus tourne, et ZERO frame est
                // arrivee. Deux causes possibles, et le produit ne sait pas trancher : le
                // processus ne presente rien en ce moment (menu, fenetre minimisee, onglet en
                // arriere-plan), ou son anti-triche bloque la capture ETW.
                //
                // Ce code s'appelait `AntiCheatBlocked`, et ce nom affirmait une cause.
                // Constate le 07/09/2026 en visant Firefox, qui n'a evidemment aucun anti-triche :
                // il ne rendait simplement rien. Le message affiche, lui, nommait deja les deux
                // hypotheses sans en choisir une : c'etait le NOM interne et son commentaire qui
                // mentaient, au point de tromper une relecture du produit.
                //
                // `NoPresentingFrames` decrit ce qu'on observe : aucune frame presentee. L'anti-
                // triche n'est qu'une hypothese parmi deux, et aucun nom interne ne doit affirmer
                // une cause que le produit n'a pas prouvee.
                if (cap.Error!.Value.Code == ErrorCodes.NoFrames && _isProcessAlive(targetPid))
                    return Result<BenchmarkResult>.Fail(ErrorCodes.NoPresentingFrames,
                        "Measure.NoPresentingFrames");

                return Result<BenchmarkResult>.Fail(cap.Error.Value.Code, cap.Error.Value.Message);
            }

            // ── Hygiene, appliquee par run et aux sept series ─────────────────────────────
            // C'est ici, et pas apres l'agregation, parce que c'est ici que les sept series sont
            // encore coherentes entre elles : `FrameTimesMs` et les six annexes sont appariees par
            // INDEX, et le ratio de la frame i se lit a l'index i de chacune. Un filtre pose sur la
            // seule serie principale decalerait les six autres, et chaque ratio du classifieur
            // deviendrait faux **sans lever la moindre erreur**.
            //
            // Le masque retire ce qui n'est pas du jeu : les secondes de chargement, et les
            // plafonds hors focus — un jeu derriere une autre fenetre se cale sur 30 fps, et
            // publier ces secondes revient a reprocher au joueur d'avoir alt-tabbe. Un vrai
            // ralentissement, lui, traverse : voir `FrameSeriesHygiene.CapRegularityMax`.
            var brut = cap.Value!.FrameTimesMs;
            var masque = FrameSeriesHygiene.Masque(brut);
            brutesVues += brut.Count;
            var frames = Filtrer(brut, masque);

            all.AddRange(frames);
            allLatencies.AddRange(Filtrer(cap.Value.RenderPresentLatencyMs, masque, brut.Count)!);
            // La serie GPU emporte son propre denominateur (10/09/2026, apres deux revues).
            //
            // Premier defaut : elle etait empilee sans appariement (`AddRange`), donc une capture
            // desynchronisee contribuait quand meme a l'occupation GPU du produit — celle qui
            // porte le verdict de goulot.
            //
            // Deuxieme defaut, plus retors : l'apparier run par run ne suffit pas. Sur cinq
            // passes dont une seule a une serie GPU decalee, `allGpuBusy` compte quatre passes et
            // `all` en compte cinq — la part appariee devient alors incalculable, et le repli
            // « moyenne sur moyenne » se rallumait sur une capture neuve, c'est-a-dire exactement
            // le melange de populations qu'on venait de retirer.
            //
            // D'ou `tramesAvecGpu` : les trames d'une passe n'entrent au denominateur que si SA
            // serie GPU est entree au numerateur. Les deux listes ne peuvent plus diverger.
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

            // Per-run aggregate for the inter-run margin (NS-6 §6): the A/B margin is the spread
            // BETWEEN independent runs, not a per-frame CI (autocorrelated frames make that far
            // too tight). A run with no usable frame contributes no run-mean — it must not poison
            // the run-to-run variance.
            var runAvg = FrametimeStatistics.AvgFps(frames);
            if (runAvg > 0)
            {
                perRunAvgFps.Add(runAvg);
                perRunP1Low.Add(FrametimeStatistics.P1LowFps(frames));
                perRunP01Low.Add(FrametimeStatistics.P01LowFps(frames));
            }
            else if (rattrapagesRestants > 0)
            {
                // Cette passe n'a rien rendu d'exploitable : elle ne compte pas, donc on en refait
                // une a la place. Sinon la resolution disparait en silence.
                rattrapagesRestants--;
                i--;
            }
        }

        if (all.Count == 0)
            return Result<BenchmarkResult>.Fail(ErrorCodes.NoFrames,
                "Aucune frame capturée sur l'ensemble des runs.");

        // Ne pas filtrer ici. L'hygiene est deja passee, par RUN, juste au-dessus — et c'est le
        // seul endroit ou elle peut passer. A ce stade `all` et les six series annexes sont le
        // recollement de plusieurs captures ; un filtre pose maintenant devrait redecouper les
        // sept a l'identique, alors que les frontieres entre runs ont disparu.
        //
        // Mesure du 07/09/2026 : rejouees a travers l'hygiene corrigee, les 8 captures reelles de
        // `tools/p3` gardent **toutes leurs trames** (0 seconde retiree sur 2 170). Elles demarrent
        // toutes en pleine partie. L'hygiene sert les captures qui commencent au LANCEMENT du jeu,
        // et les sessions ou le joueur alt-tabbe — deux cas qu'aucune donnee conservee ne contient.

        var result = new BenchmarkResult(
            AvgFps: FrametimeStatistics.AvgFps(all),
            P1LowFps: FrametimeStatistics.P1LowFps(all),
            P01LowFps: FrametimeStatistics.P01LowFps(all),
            P95FrametimeMs: FrametimeStatistics.PercentileOrNull(all, 0.95),
            FrameTimesMs: all,
            // Render-present latency (informative; present→display portion only, not system
            // latency — §5). Mean of the valid per-frame samples across runs, null when none.
            EstimatedPcLatencyMs: FrametimeStatistics.MeanOrNull(allLatencies),
            MarginOfError: FrametimeStatistics.InterRunMarginOfError(perRunAvgFps),
            P1LowMarginOfError: FrametimeStatistics.InterRunMarginOfError(perRunP1Low),
            P01LowMarginOfError: FrametimeStatistics.InterRunMarginOfError(perRunP01Low),
            Runs: runs,
            // GPU busy (ms) the GPU worked per frame, mean across runs — GpuBusy/FrameTime tells
            // GPU-bound (≈ frame time) vs CPU-bound (≪). Null when no samples (not measured).
            MeanGpuBusyMs: FrametimeStatistics.MeanOrNull(allGpuBusy),
            // L'OCCUPATION GPU, appariee. C'est elle que `GpuBusyPercent` prend maintenant comme
            // source : une part du temps ECOULE calculee somme sur somme, sur les seules trames ou
            // les deux grandeurs existent. La moyenne juste au-dessus reste publiee — elle dit
            // autre chose (le travail GPU d'une trame) — mais elle ne sert plus a fabriquer un
            // pourcentage a partir de deux populations differentes.
            GpuBusyShare: FrametimeStatistics.SumRatioOrNull(allGpuBusy, tramesAvecGpu),

            // ── L1.1 — les grandeurs du classifieur de goulot ──────────────────────────────
            // Mediane pour les ratios, p95 pour les latences : c'est ce que la spec demande, et
            // pas par gout. Une moyenne de ratios se fait tirer par quelques frames aberrantes
            // (un chargement, un alt-tab) et ferait basculer un etat entier ; un a-coup vit
            // dans la queue qu'une moyenne noie. Chaque grandeur vaut null quand elle n'a pas
            // ete mesuree -- le classifieur en tire UNKNOWN_PARTIAL, jamais un etat confiant.
            GpuWaitRatio: FrametimeStatistics.MedianRatioOrNull(allGpuWait, all),
            CpuBusyRatio: FrametimeStatistics.MedianRatioOrNull(allCpuBusy, all),
            PresentStallMsP95: FrametimeStatistics.PercentileOrNull(allPresentApi, 0.95),
            // Le compte qui va avec, par le meme filtre : la valeur et sa garde ne peuvent pas
            // porter sur deux populations differentes.
            PresentStallSamples: FrametimeStatistics.EchantillonsExploitables(allPresentApi),
            RenderPresentLatencyMsP95: FrametimeStatistics.PercentileOrNull(allLatencies, 0.95),
            // Valeur absolue : une frame affichee 5 ms trop TOT casse la cadence autant qu'une
            // frame 5 ms trop tard, et l'erreur d'animation est signee.
            AnimationErrorMsP95: FrametimeStatistics.AbsPercentileOrNull(allAnimationError, 0.95),
            // Le compte qui va avec ce p95, calcule par le meme filtre : sans lui, l'affichage
            // jugerait son interpretabilite sur le nombre de trames, qui n'a aucun rapport avec le
            // nombre d'echantillons d'animation reellement retenus.
            AnimationErrorSamples: FrametimeStatistics.EchantillonsAbsExploitables(allAnimationError),
            // La seule grandeur qui ne vient pas de PresentMon. Elle parle de fiabilite (gels,
            // OOM), pas de FPS, et c'est exactement pour ca qu'elle compte : une machine sous
            // pression de validation ne se repare pas en overclockant quoi que ce soit.
            CommitPressure: _commitPressure(),

            // L'exposition reelle de la mesure : ce que l'hygiene a garde, tous runs confondus.
            // Sans elle, deux passes A/B peuvent avoir vecu des choses differentes sans que rien
            // ne le dise — un alt-tab de vingt secondes d'un seul cote suffit a fabriquer un gain.
            // `AbComparer.Evidence` refuse de comparer deux expositions trop eloignees.
            HygieneKeptFraction: brutesVues > 0 ? (double)all.Count / brutesVues : null,

            // Le focus observe. Sans sonde, il reste null — et Unknown ne condamne pas.
            ForegroundFraction: focus.FractionAuPremierPlan,
            Materiel: HardwareObservation.De(lecturesMateriel.ToList()));

        return Result<BenchmarkResult>.Ok(result);
    }

    // Ajoute une serie a son accumulateur seulement si elle a bien une entree par frame.
    // Une serie partielle concatenee decalerait tout ce qui suit sans qu'aucun test ne rougisse :
    // les ratios continueraient d'etre calcules, sur des paires de frames differentes.
    private static void AppendAligned(List<double> accumulator, IReadOnlyList<double>? series, int frameCount)
    {
        if (series is null || series.Count != frameCount) return;
        accumulator.AddRange(series);
    }

    /// <summary>Applique le masque d'hygiene a la serie PRINCIPALE.</summary>
    private static IReadOnlyList<double> Filtrer(
        IReadOnlyList<double> serie, FrameSeriesHygiene.MaskResult masque)
        => Filtrer(serie, masque, serie.Count)!;

    /// <summary>Applique le masque a une serie ANNEXE.
    ///
    /// Une serie annexe dont la longueur ne correspond pas a la serie brute est rendue TELLE
    /// QUELLE, sans etre filtree : elle n'etait deja pas appariee, et lui appliquer un masque
    /// construit sur d'autres index inventerait un alignement au lieu de le retablir.
    /// `AppendAligned` la rejettera ensuite, ce qui est le comportement voulu — une serie
    /// desynchronisee ne doit contribuer a aucun ratio.</summary>
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
