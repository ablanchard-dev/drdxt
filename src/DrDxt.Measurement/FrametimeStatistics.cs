using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>
/// Stats frametime (méthodo §4 P1). 1%/0.1% low = moyenne du pire centile de FRAMES (façon GamersNexus/CapFrameX),
/// exprimée en FPS. Marge d'erreur = IC 95% de la moyenne (t≈1.96 grand n). Code pur, aucun I/O.
/// Garde-fou d'entrée : les frametimes invalides (≤ 0, NaN, Inf — ex. frame droppée PresentMon) sont
/// filtrés avant tout calcul, pour qu'une ligne aberrante ne corrompe pas silencieusement un résultat.
/// </summary>
public static class FrametimeStatistics
{
    private static List<double> Clean(IReadOnlyList<double> ft)
        => ft.Where(x => x > 0 && double.IsFinite(x)).ToList();

    public static double AvgFps(IReadOnlyList<double> frameTimesMs)
    {
        var ft = Clean(frameTimesMs);
        if (ft.Count == 0) return 0;
        var meanMs = ft.Average();
        return meanMs <= 0 ? 0 : 1000.0 / meanMs;
    }

    public static double P1LowFps(IReadOnlyList<double> ft) => PercentileLowFps(ft, 0.01);
    public static double P01LowFps(IReadOnlyList<double> ft) => PercentileLowFps(ft, 0.001);

    private static double PercentileLowFps(IReadOnlyList<double> input, double fraction)
    {
        var ft = Clean(input);
        if (ft.Count == 0) return 0;
        // Pire X% des frames = frametimes les plus LONGS.
        var sortedDesc = ft.OrderByDescending(x => x).ToList();
        var count = Math.Max(1, (int)Math.Ceiling(ft.Count * fraction));
        var worst = sortedDesc.Take(count).ToList();
        var meanWorstMs = worst.Average();
        return meanWorstMs <= 0 ? 0 : 1000.0 / meanWorstMs;
    }

    /// <summary>
    /// 95% confidence margin (±, in the same unit as the inputs — e.g. FPS) on the mean across
    /// RUNS, derived from the run-to-run spread of the per-run aggregates. This is the HONEST A/B
    /// margin: successive frametimes are autocorrelated (a stutter spans many frames), so a
    /// per-frame CI treats n correlated frames as n independent samples and comes out far too
    /// tight → false "measurable gains". The relevant noise is between independent runs
    /// (GamersNexus-style multi-pass). Returns +Infinity with fewer than 3 valid runs ("gain not
    /// provable"), so <see cref="AbComparer"/> can never claim a gain on unreliable data.
    /// Source: the project's measurement-proof note (unpublished), §6.
    /// </summary>
    public static double InterRunMarginOfError(IReadOnlyList<double> perRunValues)
    {
        var runs = perRunValues.Where(x => x > 0 && double.IsFinite(x)).ToList();
        if (runs.Count < 3) return double.PositiveInfinity;
        var mean = runs.Average();
        var variance = runs.Sum(x => (x - mean) * (x - mean)) / (runs.Count - 1);
        var stdErr = Math.Sqrt(variance) / Math.Sqrt(runs.Count);
        return TValue95(runs.Count - 1) * stdErr;
    }

    // 26/09/2026 : la table vit une seule fois (DrDxt.Core.Student). Celle-ci retombait sur
    // 1,96 au-dela de 20 degres de liberte, une marge trop etroite.
    private static double TValue95(int df) => DrDxt.Core.Student.T95(df);

    /// <summary>
    /// Mean of the strictly-positive, finite values, or <c>null</c> when there are none. Used for
    /// informative aggregates (e.g. render-present latency) where "no valid sample" must read as
    /// "not measured" (null), never a fabricated 0.
    /// </summary>
    public static double? MeanOrNull(IReadOnlyList<double> values)
    {
        var v = values.Where(x => x > 0 && double.IsFinite(x)).ToList();
        return v.Count == 0 ? null : v.Average();
    }

    // ── L1.1 — les agregats que le classifieur de goulot demande ────────────────────────────
    // Source : la note de conception du classifieur (voir `BottleneckClassifier`), § « Derived
    // metrics to compute ». La spec dit mediane pour les ratios et p95 pour les latences ; ce
    // ne sont pas des details de gout. Une moyenne de ratios se fait tirer par quelques frames
    // aberrantes (un chargement, un alt-tab) et ferait basculer un etat entier ; un p95 est
    // justement la ou vit un a-coup, qu'une moyenne noie.

    /// <summary>
    /// Mediane du ratio par FRAME <paramref name="numerators"/> / <paramref name="denominators"/>,
    /// bornee a [0, 1], ou <c>null</c> si on ne peut pas la calculer honnetement.
    ///
    /// Rend null — jamais 0 — quand les deux series n'ont pas la meme longueur : elles sont
    /// censees etre paralleles (une entree par frame), et si elles ne le sont pas, apparier
    /// l'index i de l'une avec l'index i de l'autre divise deux frames differentes. Le resultat
    /// aurait l'air d'un ratio et n'en serait pas.
    ///
    /// Le bornage a 1 est deliberé : la doc PresentMon previent que le temps GPU actif est
    /// biaise a la hausse sous HAGS, donc un ratio > 1 est un artefact de mesure, pas un GPU
    /// qui travaille 110 % de la frame.
    /// </summary>
    public static double? MedianRatioOrNull(
        IReadOnlyList<double>? numerators, IReadOnlyList<double>? denominators)
    {
        if (numerators is null || denominators is null) return null;
        if (numerators.Count == 0 || numerators.Count != denominators.Count) return null;

        var ratios = new List<double>(numerators.Count);
        for (var i = 0; i < numerators.Count; i++)
        {
            var d = denominators[i];
            var n = numerators[i];
            // Un denominateur nul ou non fini n'est pas une frame exploitable. Un numerateur
            // negatif non plus (0 est valide : le GPU peut n'avoir rien attendu).
            if (!(d > 0) || !double.IsFinite(d) || !double.IsFinite(n) || n < 0) continue;
            ratios.Add(Math.Min(1.0, n / d));
        }

        return ratios.Count == 0 ? null : Median(ratios);
    }

    /// <summary>
    /// La part du temps total, appariee trame par trame : somme des numerateurs sur somme des
    /// denominateurs, et seulement sur les trames ou les deux sont exploitables.
    ///
    /// Pourquoi pas une moyenne divisee par une autre moyenne. Les deux moyennes ne portent pas
    /// forcement sur les memes trames : celle du numerateur ecarte les echantillons non finis et
    /// les zeros, celle du denominateur garde toutes les trames. Le rapport melange alors deux
    /// populations — et un zero de GPU-busy, qui est une mesure parfaitement valide (le GPU n'a
    /// rien fait sur cette trame), disparait du haut sans disparaitre du bas.
    ///
    /// Ce n'est pas la mediane des rapports par trame : celle-la repond a « a quoi ressemble
    /// une trame typique », celle-ci a « quelle part du temps ecoule ». Un plafond de DEBIT se
    /// calcule sur des totaux.
    /// </summary>
    public static double? SumRatioOrNull(
        IReadOnlyList<double>? numerators, IReadOnlyList<double>? denominators)
    {
        if (numerators is null || denominators is null) return null;
        if (numerators.Count == 0 || numerators.Count != denominators.Count) return null;

        double haut = 0, bas = 0;
        for (var i = 0; i < numerators.Count; i++)
        {
            var d = denominators[i];
            var n = numerators[i];
            // Meme regle de validite qu'ailleurs : un denominateur nul ou non fini n'est pas une
            // trame exploitable, un numerateur negatif non plus. Zero est valide.
            if (!(d > 0) || !double.IsFinite(d) || !double.IsFinite(n) || n < 0) continue;
            haut += n;
            bas += d;
        }

        return bas > 0 ? haut / bas : null;
    }

    /// <summary>
    /// Percentile (0..1) des valeurs finies et positives, ou <c>null</c> quand il n'y en a
    /// aucune. Interpolation la plus proche sur la serie triee — assez pour un p95 de latence,
    /// et sans pretendre a une precision que le nombre de frames ne porte pas.
    /// </summary>
    public static double? PercentileOrNull(IReadOnlyList<double>? values, double fraction)
    {
        if (values is null) return null;
        // Le meme filtre que `Clean` et que `EchantillonsExploitables` : ecrit une seule fois, il
        // ne peut pas diverger du compte qui sert a juger si ce percentile merite son nom.
        var v = Clean(values).OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        var index = (int)Math.Ceiling(fraction * v.Count) - 1;
        return v[Math.Clamp(index, 0, v.Count - 1)];
    }

    /// <summary>
    /// Percentile de la valeur absolue — pour l'erreur d'animation, qui est signee : une frame
    /// affichee 5 ms trop TOT est aussi mauvaise pour la cadence qu'une frame 5 ms trop tard, et
    /// filtrer sur « > 0 » jetterait la moitie du signal.
    /// </summary>
    public static double? AbsPercentileOrNull(IReadOnlyList<double>? values, double fraction)
        => values is null ? null : PercentileOrNull(values.Select(Math.Abs).ToList(), fraction);

    /// <summary>
    /// Combien d'echantillons portent reellement un percentile — la question sans laquelle on ne
    /// sait pas si le mot « p95 » est merite.
    ///
    /// Le defaut qu'elle repare : la garde d'interpretabilite comptait les FRAMETIMES, alors
    /// que le p95 de l'erreur d'animation repose sur une autre serie. Une passe dont la serie
    /// d'animation est desynchronisee ou absente ne contribue pas au numerateur, et le compte des
    /// trames n'en sait rien : on aurait donc valide un p95 tire de huit points en lisant mille
    /// trames. Meme faute que deux moyennes prises sur des trames differentes.
    ///
    /// Applique exactement le filtre de <see cref="PercentileOrNull"/> — sur la valeur absolue,
    /// comme <see cref="AbsPercentileOrNull"/>. Les deux doivent bouger ensemble : un compte qui
    /// ne compte pas ce que le percentile garde ne verifie rien.
    /// </summary>
    public static int EchantillonsAbsExploitables(IReadOnlyList<double>? values)
        => values is null ? 0 : values.Count(x => Math.Abs(x) > 0 && double.IsFinite(x));

    /// <summary>
    /// Combien de valeurs portent reellement un <see cref="PercentileOrNull"/> — le compte a
    /// donner a la garde d'interpretabilite quand on affiche un p95.
    ///
    /// Meme filtre que le percentile, par construction : les deux passent par <c>Clean</c>. Un
    /// compte qui ne compte pas ce que le percentile garde ne verifie rien, et ce depot a paye la
    /// lecon deux fois — d'abord sur une occupation GPU calculee sur deux populations de trames,
    /// puis sur un p95 d'animation juge au nombre de trames.
    /// </summary>
    public static int EchantillonsExploitables(IReadOnlyList<double>? values)
        => values is null ? 0 : Clean(values).Count;

    // 27/09/2026 : les trois queues que l'écran affiche (domaine Latence), chacune avec la garde de SA série.
    // Une seule source pour l'écran et le rapport de diagnostic, qui ne les portait pas. null = pas mesurée,
    // ou trop peu d'échantillons pour qu'un p95 mérite son nom.

    /// <summary>Les 5 % de trames les plus lentes : le p95 des frametimes, jugé sur la même liste filtrée.</summary>
    public static double? SaccadeP95(BenchmarkResult? mesure)
        => mesure?.P95FrametimeMs is { } p95
           && TailStatistic.P95EstInterpretable(EchantillonsExploitables(mesure.FrameTimesMs)) ? p95 : null;

    /// <summary>L'écart d'animation p95 (cadence), jugé sur le compte de sa propre série.</summary>
    public static double? CadenceP95(BenchmarkResult? mesure)
        => mesure?.AnimationErrorMsP95 is { } p95 && mesure.AnimationErrorSamples is { } n
           && TailStatistic.P95EstInterpretable(n) ? p95 : null;

    /// <summary>L'attente d'affichage p95, jugée sur le compte de sa propre série.</summary>
    public static double? AttenteP95(BenchmarkResult? mesure)
        => mesure?.PresentStallMsP95 is { } p95 && mesure.PresentStallSamples is { } n
           && TailStatistic.P95EstInterpretable(n) ? p95 : null;

    /// <summary>
    /// Coefficient de variation des frametimes (ecart-type / moyenne) : le proxy de cadence
    /// Normalise, qui compare une machine a 60 FPS et une a 240 FPS sur la meme echelle. Null
    /// quand il y a moins de deux frames valides — une seule frame n'a pas de dispersion, et en
    /// rendre 0 dirait « parfaitement regulier » sur une absence de mesure.
    /// </summary>
    public static double? CoefficientOfVariationOrNull(IReadOnlyList<double> frameTimesMs)
    {
        var ft = Clean(frameTimesMs);
        if (ft.Count < 2) return null;
        var mean = ft.Average();
        if (mean <= 0) return null;
        var variance = ft.Sum(x => (x - mean) * (x - mean)) / (ft.Count - 1);
        return Math.Sqrt(variance) / mean;
    }

    /// <summary>
    /// Une passe reconstruite depuis une serie deja enregistree — rejeu, re-analyse hors ligne.
    ///
    /// Pourquoi ce point de passage existe, et ce qu'il a coute de ne pas l'avoir. Les bancs de
    /// rejeu (`tools/optimize`, `tools/smoke`) construisaient leur <see cref="BenchmarkResult"/> a
    /// la main, EN POSITIONNEL. Les grandeurs optionnelles du contrat sont en queue avec un
    /// `= null` par defaut : en oublier une ne casse rien, ne leve rien, et ne rougit nulle part.
    /// C'est exactement ce qui est arrive au p95 — `GameMetricSet.Valeur` rend `0` sur un p95 nul,
    /// donc le rejeu du 11/09/2026 a servi de reference pendant deux jours en ne jugeant que deux
    /// grandeurs sur trois, en annoncant les trois.
    ///
    /// Les formules sont celles de <see cref="BenchmarkRunner"/> et rien d'autre : le meme p95,
    /// les memes lows. Un banc qui passe par ici ne peut plus perdre une grandeur en
    /// silence — et la garantie, elle, est testee.
    ///
    /// La latence et la telemetrie GPU restent nulles : une serie de frametimes enregistree ne
    /// les contient pas. Null = pas mesuree, jamais zero.
    /// </summary>
    /// <param name="frameTimesMs">La serie brute, en millisecondes.</param>
    /// <param name="runs">Combien de passes (ou de tranches) ont produit cette serie.</param>
    /// <param name="marginOfError">La marge inter-passes ; l'appelant seul sait sur quoi la calculer.</param>
    /// <param name="hygieneKeptFraction">La part conservee par l'hygiene, quand l'appelant la connait.</param>
    public static BenchmarkResult PasseDepuisSerie(
        IReadOnlyList<double> frameTimesMs,
        int runs,
        double marginOfError,
        double? hygieneKeptFraction = null)
        => new(
            AvgFps: AvgFps(frameTimesMs),
            P1LowFps: P1LowFps(frameTimesMs),
            P01LowFps: P01LowFps(frameTimesMs),
            FrameTimesMs: frameTimesMs,
            EstimatedPcLatencyMs: null,
            MarginOfError: marginOfError,
            Runs: runs,
            P95FrametimeMs: PercentileOrNull(frameTimesMs, 0.95),
            HygieneKeptFraction: hygieneKeptFraction);

    private static double Median(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}
