using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>
/// Stats frametime. 1%/0.1% low = moyenne du pire centile de frames (façon GamersNexus/CapFrameX),
/// exprimée en FPS. Marge d'erreur = IC 95% de la moyenne (t de Student selon les degrés de liberté). Code pur, aucun I/O.
/// Garde-fou d'entrée : les frametimes invalides (≤ 0, NaN, Inf, ex. frame droppée PresentMon) sont
/// filtrés avant tout calcul, pour qu'une ligne aberrante ne corrompe pas silencieusement un résultat.
/// </summary>
public static class FrametimeStatistics
{
    /// <summary>
    /// La part des images que le compositeur de Windows a recopiées (`PM_PRESENT_MODE` 4, 5 et 6 de l'en-tête PresentMon
    /// livré dans `third_party/presentmon`), sur les images dont le mode est connu (0 = inconnu, écarté). Le retournement
    /// indépendant, matériel ou par superposition (3 et 8), n'est pas composé. null = aucun mode connu.
    /// </summary>
    public static double? PartComposee(IReadOnlyList<int>? modes)
    {
        var connus = modes?.Where(m => m != 0).ToList();
        if (connus is null || connus.Count == 0) return null;
        return connus.Count(m => m is 4 or 5 or 6) / (double)connus.Count;
    }

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
        // Pire X% des frames = frametimes les plus longs.
        var sortedDesc = ft.OrderByDescending(x => x).ToList();
        var count = Math.Max(1, (int)Math.Ceiling(ft.Count * fraction));
        var worst = sortedDesc.Take(count).ToList();
        var meanWorstMs = worst.Average();
        return meanWorstMs <= 0 ? 0 : 1000.0 / meanWorstMs;
    }

    /// <summary>
    /// Marge de confiance à 95 % (±, dans l'unité des entrées) sur la moyenne des passes, tirée de l'écart entre passes.
    /// Les temps d'image successifs sont autocorrélés (un à-coup couvre plusieurs images) : un intervalle par image
    /// serait bien trop étroit, d'où de faux « gains mesurables » ; le bruit qui compte est entre passes indépendantes
    /// (multipasse façon GamersNexus). +Infinity sous 3 passes valides : <see cref="AbComparer"/> n'annonce alors aucun
    /// gain. Source : la note de preuve de mesure du projet (non publiée).
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

    // La table vit une seule fois (DrDxt.Core.Student) : retomber sur 1,96 au-dela de 20 degres de liberte serait trop
    // etroit.
    private static double TValue95(int df) => DrDxt.Core.Student.T95(df);

    /// <summary>
    /// Moyenne des valeurs finies strictement positives, ou <c>null</c> : « aucun relevé valide » se lit « non mesuré »,
    /// jamais un 0 fabriqué (latence rendu-présentation, par exemple).
    /// </summary>
    public static double? MeanOrNull(IReadOnlyList<double> values)
    {
        var v = values.Where(x => x > 0 && double.IsFinite(x)).ToList();
        return v.Count == 0 ? null : v.Average();
    }

    // ── Les agregats que le classifieur de goulot demande ────────────────────────────
    // Note de conception du classifieur (`BottleneckClassifier`), § « Derived metrics to compute » : mediane pour les
    // ratios (une moyenne se fait tirer par un chargement, un alt-tab), p95 pour les latences (la ou vit un a-coup).

    /// <summary>
    /// Mediane du ratio par frame <paramref name="numerators"/> / <paramref name="denominators"/>, bornee a [0, 1].
    /// null, jamais 0, si les longueurs different : apparier l'index i des deux diviserait deux frames differentes.
    /// Borne a 1 : selon la doc PresentMon, le temps GPU actif est biaise a la hausse sous HAGS.
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
            // Denominateur nul ou non fini, numerateur negatif : frame inexploitable (0 est valide).
            if (!(d > 0) || !double.IsFinite(d) || !double.IsFinite(n) || n < 0) continue;
            ratios.Add(Math.Min(1.0, n / d));
        }

        return ratios.Count == 0 ? null : Median(ratios);
    }

    /// <summary>
    /// La part du temps total, appariee trame par trame (somme sur somme, trames exploitables des deux cotes). Un rapport
    /// de deux moyennes melangerait deux populations : un zero de GPU-busy, valide, disparaitrait du haut et pas du bas.
    /// La mediane des rapports dit « une trame typique », celle-ci « quelle part du temps » : un plafond de debit se
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
            // Meme regle de validite qu'ailleurs. Zero est valide.
            if (!(d > 0) || !double.IsFinite(d) || !double.IsFinite(n) || n < 0) continue;
            haut += n;
            bas += d;
        }

        return bas > 0 ? haut / bas : null;
    }

    /// <summary>
    /// Percentile (0..1) des valeurs finies et positives, ou <c>null</c>. Rang le plus proche : assez pour un p95 de
    /// latence, sans precision que le nombre de frames ne porte pas.
    /// </summary>
    public static double? PercentileOrNull(IReadOnlyList<double>? values, double fraction)
    {
        if (values is null) return null;
        // Le filtre de `EchantillonsExploitables`, ecrit une seule fois : il ne peut pas diverger du compte.
        var v = Clean(values).OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        var index = (int)Math.Ceiling(fraction * v.Count) - 1;
        return v[Math.Clamp(index, 0, v.Count - 1)];
    }

    /// <summary>
    /// Percentile de la valeur absolue, pour l'erreur d'animation signee : 5 ms trop tot vaut 5 ms trop tard, et
    /// filtrer sur « > 0 » jetterait la moitie du signal.
    /// </summary>
    public static double? AbsPercentileOrNull(IReadOnlyList<double>? values, double fraction)
        => values is null ? null : PercentileOrNull(values.Select(Math.Abs).ToList(), fraction);

    /// <summary>
    /// Combien d'echantillons portent reellement un percentile absolu. Compter les frametimes se tromperait : le p95
    /// d'animation repose sur une autre serie, et on validerait un p95 de huit points en lisant mille trames. Meme filtre
    /// que <see cref="AbsPercentileOrNull"/> : les deux bougent ensemble.
    /// </summary>
    public static int EchantillonsAbsExploitables(IReadOnlyList<double>? values)
        => values is null ? 0 : values.Count(x => Math.Abs(x) > 0 && double.IsFinite(x));

    /// <summary>
    /// Combien de valeurs portent reellement un <see cref="PercentileOrNull"/>, pour la garde d'interpretabilite d'un p95.
    /// Les deux passent par <c>Clean</c> : un compte qui ne compte pas ce que le percentile garde ne verifie rien.
    /// </summary>
    public static int EchantillonsExploitables(IReadOnlyList<double>? values)
        => values is null ? 0 : Clean(values).Count;

    // Les trois queues de l'écran (domaine Latence), chacune gardée sur sa série ; une seule source pour l'écran et le
    // rapport de diagnostic. null = pas mesurée, ou trop peu d'échantillons pour un p95.

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
    /// Une passe reconstruite depuis une serie enregistree (rejeu, re-analyse). Construire un
    /// <see cref="BenchmarkResult"/> a la main (`tools/optimize`, `tools/smoke`) laisse oublier une grandeur optionnelle :
    /// un p95 oublie vaut `0` dans `GameMetricSet.Valeur`. Ici, les formules de <see cref="BenchmarkRunner"/>, garantie
    /// testee. Latence et telemetrie GPU restent nulles : une serie de frametimes ne les contient pas.
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
