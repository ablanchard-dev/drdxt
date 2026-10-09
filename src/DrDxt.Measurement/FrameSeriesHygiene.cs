namespace DrDxt.Measurement;

/// <summary>
/// Frame-series hygiene before any statistic is computed (§4 P1 companion to
/// <see cref="FrametimeStatistics"/>). Two operations, both pure, no I/O:
///
///   • <see cref="Clean"/> drops the frames that are not gameplay — the loading warm-up and the
///     seconds the game spent behind another window (alt-tab). Published raw, those seconds are
///     reported as defects of the player's machine. Measured on a 39-minute reference capture:
///     0.1% low 33.87 ms raw → 18.54 ms clean, p99 latency 63.0 → 14.2 ms — a factor 4.4 on the
///     worst percentile, from frames that were never gameplay.
///
///   • <see cref="Slice"/> cuts a long session into equal-duration slices. Extreme percentiles
///     degrade mechanically with duration, so comparing a 3.4 h session to a 39 min one is rigged.
///     Slicing yields the machine's own run-to-run dispersion, which is exactly the input
///     <see cref="FrametimeStatistics.InterRunMarginOfError"/> needs — so ONE long session can
///     produce an honest A/B margin instead of demanding three separate benchmark runs.
///
/// There are no timestamps in <see cref="PresentMon.FrameCapture"/> and none are needed: the frame
/// times ARE the deltas, so elapsed time is their running sum.
/// </summary>
public static class FrameSeriesHygiene
{
    /// <summary>UPPER BOUND on the seconds discarded at the start of a capture (level load, shader
    /// compilation) — not the amount actually dropped. The real warm-up is MEASURED by
    /// <see cref="DetectWarmup"/>; this only caps how far that search may go.
    ///
    /// It used to be the amount dropped, flat. Measured on a 178 s capture taken while already in
    /// a match (2026-09-01, i7-14700F / RTX 4060): second 0 was already at 7.84 ms against a 7.27 ms
    /// session median — there was no warm-up at all. Dropping 90 s flat threw away half the frames
    /// (24 082 → 11 783) for a 0.9 % change in the result, and left 88 s — too short for even one
    /// 120 s slice, so <see cref="Slice"/> could produce nothing. A capture that STARTS in gameplay
    /// has nothing to warm up; only one that starts at game launch does.</summary>
    public const double DefaultWarmupSeconds = 90.0;

    /// <summary>A leading second counts as warm-up while its median frame time exceeds this many
    /// times the session median. Calibrated against the same series rather than a fixed millisecond
    /// value, so it transfers between machines: a 30 fps loading second is ~4.6× a 150 fps gameplay
    /// second here, while ordinary gameplay jitter stays near 1.1×. Verified on the capture above —
    /// the detected warm-up is 0 s at 1.25×, 1.5× and 2×, so the choice is not load-bearing.</summary>
    public const double WarmupMedianFactor = 1.5;

    /// <summary>A whole second whose MEDIAN frame time exceeds this MAY be out of gameplay: the
    /// out-of-focus cap (a game capped at 30 fps out of focus sits at 33.3 ms) or a load stall.
    /// The median — not the max — so a single legitimate stutter never deletes a good second.
    /// calibration knob: read the game's own out-of-focus cap when it is knowable
    /// (Call of Duty publishes <c>MaxFpsOutOfFocus</c>) and set this just under that period,
    /// instead of guessing.
    ///
    /// Slow is not sufficient — see <see cref="CapRegularityMax"/>. On its own this threshold
    /// deleted 10 seconds of a genuine 25 fps stutter (proved by a positive control).</summary>
    public const double DefaultOutOfFocusMedianMs = 25.0;

    /// <summary>
    /// Slow is not enough to delete a second — it also has to be *REGULAR*.
    ///
    /// A second is discarded only when its interquartile range, relative to its median, stays below
    /// this. Rationale, and it is structural rather than statistical: a game **out of focus is
    /// capped** — the engine sleeps until the next tick, so the period is regular by construction.
    /// A game that is genuinely **struggling** is limited by variable work, so its frame times
    /// scatter. Median alone cannot tell a 30 fps cap from a 25 fps stutter; regularity can.
    ///
    /// The value is anchored in a measurement, not in judgement. Across the **2 170 seconds** of
    /// real gameplay kept in `tools/p3` (2026-09-01, i7-14700F / RTX 4060, ~143 fps), the single
    /// most regular second scores **0.038**; the 1st percentile is 0.060 and the median 0.128.
    /// Real gameplay simply never gets this even. The threshold sits at that observed floor, so
    /// every doubt resolves the safe way — **keep the data**. The project's rule: a sustained slowdown is
    /// the result of the measurement, never an artefact to clean away.
    ///
    /// Known ceiling: a cap whose timer jitter exceeds 4 % is kept rather than dropped, and those
    /// seconds then count as the machine's performance. That is the deliberate direction — the
    /// opposite error publishes a healthy machine to a player who watched his game crawl.
    /// The honest fix is to observe the window state during capture; until then this is an
    /// inference, and it is labelled as one.
    /// </summary>
    public const double CapRegularityMax = 0.04;

    /// <summary>
    /// Result of <see cref="Clean"/>. The counts exist so a caller can SEE what was removed:
    /// a filter that silently eats a capture is worse than no filter.
    /// </summary>
    /// <param name="FrameTimesMs">The frames kept, in order.</param>
    /// <param name="WarmupSecondsDropped">Whole seconds dropped as warm-up.</param>
    /// <param name="OutOfFocusSecondsDropped">Whole seconds dropped as out-of-focus / stalled.</param>
    /// <param name="Applied">False when the filter refused to run — see <see cref="Clean"/>.</param>
    public sealed record CleanResult(
        IReadOnlyList<double> FrameTimesMs,
        int WarmupSecondsDropped,
        int OutOfFocusSecondsDropped,
        bool Applied);

    /// <summary>
    /// Drops warm-up seconds and out-of-focus seconds from a frame series.
    ///
    /// REFUSES to apply (returns the input untouched with <c>Applied = false</c>) when the filter
    /// would keep less than <paramref name="minKeptFraction"/> of the frames. That guard is the
    /// point: on a machine genuinely running at 30 fps every second has a median above the
    /// threshold, and a filter that deleted the whole session would turn a real problem into an
    /// empty result. Refusing is a THIRD state — neither "filtered" nor "nothing to filter".
    /// </summary>
    public static CleanResult Clean(
        IReadOnlyList<double> frameTimesMs,
        double warmupSeconds = DefaultWarmupSeconds,
        double outOfFocusMedianMs = DefaultOutOfFocusMedianMs,
        double minKeptFraction = 0.20)
    {
        var masque = Masque(frameTimesMs, warmupSeconds, outOfFocusMedianMs, minKeptFraction);
        if (!masque.Applied)
            return new CleanResult(frameTimesMs ?? Array.Empty<double>(), 0, 0, false);

        var gardees = new List<double>(frameTimesMs!.Count);
        for (var i = 0; i < frameTimesMs.Count; i++)
            if (masque.Garde[i]) gardees.Add(frameTimesMs[i]);

        return new CleanResult(gardees, masque.WarmupSecondsDropped, masque.OutOfFocusSecondsDropped, true);
    }

    /// <summary>
    /// Le meme nettoyage, rendu comme un masque — parce que sept series doivent bouger ensemble.
    ///
    /// `BenchmarkRunner` accumule `FrameTimesMs` **et six series appariees par index**
    /// (latence, GPU busy, GPU wait, CPU busy, present API, erreur d'animation). Le ratio de la
    /// frame i se lit a l'index i de chacune. Filtrer la premiere seule decale les six autres, et
    /// chaque ratio du classifieur devient faux **sans lever la moindre erreur**.
    ///
    /// <see cref="Clean"/> rend une serie, ce qui suffit a un rejeu hors ligne et **pas** a la
    /// production. Cette surcharge rend la DECISION, index par index, pour que l'appelant
    /// l'applique aux sept series d'un meme run — la ou elles sont encore coherentes entre elles.
    ///
    /// <paramref name="frameTimesMs"/> et le masque ont exactement la meme longueur : une frame
    /// invalide (nulle, negative, non finie) est marquee non gardee plutot que sautee, sinon les
    /// index ne voudraient plus rien dire.
    /// </summary>
    /// <param name="Garde">Un booleen par frame d'entree : la garder, ou non.</param>
    /// <param name="Applied">False quand le filtre a refuse — le masque est alors tout a vrai.</param>
    public sealed record MaskResult(
        IReadOnlyList<bool> Garde,
        int WarmupSecondsDropped,
        int OutOfFocusSecondsDropped,
        bool Applied);

    /// <inheritdoc cref="MaskResult"/>
    public static MaskResult Masque(
        IReadOnlyList<double> frameTimesMs,
        double warmupSeconds = DefaultWarmupSeconds,
        double outOfFocusMedianMs = DefaultOutOfFocusMedianMs,
        double minKeptFraction = 0.20)
    {
        if (frameTimesMs is null || frameTimesMs.Count == 0)
            return new MaskResult(Array.Empty<bool>(), 0, 0, false);

        return CleanCore(frameTimesMs, warmupSeconds, outOfFocusMedianMs, minKeptFraction);
    }

    private static MaskResult CleanCore(
        IReadOnlyList<double> frameTimesMs,
        double warmupSeconds,
        double outOfFocusMedianMs,
        double minKeptFraction)
    {
        var toutGarde = () => new MaskResult(
            Enumerable.Repeat(true, frameTimesMs.Count).ToList(), 0, 0, false);

        // Bucket frames by the whole second they fall in. Invalid frames still advance nothing:
        // they carry no duration we can trust, so they are dropped and never shift the clock.
        // `secondeDe` garde la seconde de chaque index d'entree (−1 = frame invalide), parce
        // que le masque rendu doit avoir exactement la longueur de la serie d'entree : sans ca,
        // les index ne designeraient plus les memes frames dans les sept series.
        var bySecond = new Dictionary<int, List<double>>();
        var secondeDe = new int[frameTimesMs.Count];
        var valides = 0;
        var elapsedMs = 0.0;
        for (var i = 0; i < frameTimesMs.Count; i++)
        {
            var ms = frameTimesMs[i];
            if (!(ms > 0) || !double.IsFinite(ms)) { secondeDe[i] = -1; continue; }
            var second = (int)(elapsedMs / 1000.0);
            elapsedMs += ms;
            if (!bySecond.TryGetValue(second, out var bucket))
                bySecond[second] = bucket = new List<double>();
            bucket.Add(ms);
            secondeDe[i] = second;
            valides++;
        }

        if (valides == 0)
            return toutGarde();

        var warmupCutoff = DetectWarmup(bySecond, (int)Math.Max(0, warmupSeconds));
        var warmupDropped = 0;
        var focusDropped = 0;
        var dropped = new HashSet<int>();
        foreach (var (second, bucket) in bySecond)
        {
            if (second < warmupCutoff)
            {
                dropped.Add(second);
                warmupDropped++;
            }
            else if (Median(bucket) > outOfFocusMedianMs && EstUnPlafond(bucket))
            {
                dropped.Add(second);
                focusDropped++;
            }
        }

        var garde = new bool[frameTimesMs.Count];
        var gardees = 0;
        for (var i = 0; i < frameTimesMs.Count; i++)
        {
            // Une frame invalide n'est jamais gardee : elle ne porte aucune duree exploitable.
            garde[i] = secondeDe[i] >= 0 && !dropped.Contains(secondeDe[i]);
            if (garde[i]) gardees++;
        }

        // La garde de dernier recours : sur une machine qui tourne genuinement au ralenti, tout
        // supprimer transformerait le pire diagnostic en resultat vide. Refuser est un TROISIEME
        // etat — ni « filtre », ni « rien a filtrer ».
        return gardees < valides * minKeptFraction
            ? toutGarde()
            : new MaskResult(garde, warmupDropped, focusDropped, true);
    }

    /// <summary>
    /// Cuts a frame series into consecutive slices of <paramref name="sliceSeconds"/> each. A
    /// trailing remainder shorter than the slice is DISCARDED — keeping it would compare a short
    /// slice against full ones, which is the very bias slicing exists to remove.
    /// Returns an empty list when the series is shorter than one slice (say so; never pad).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<double>> Slice(
        IReadOnlyList<double> frameTimesMs, double sliceSeconds)
    {
        var slices = new List<IReadOnlyList<double>>();
        if (frameTimesMs is null || frameTimesMs.Count == 0 || !(sliceSeconds > 0))
            return slices;

        var sliceMs = sliceSeconds * 1000.0;
        var current = new List<double>();
        var inSlice = 0.0;
        foreach (var ms in frameTimesMs)
        {
            if (!(ms > 0) || !double.IsFinite(ms)) continue;
            current.Add(ms);
            inSlice += ms;
            if (inSlice < sliceMs) continue;
            slices.Add(current);
            current = new List<double>();
            inSlice = 0.0;
        }

        return slices; // `current` is the short remainder — deliberately dropped.
    }

    /// <summary>
    /// How many LEADING whole seconds are warm-up, measured against the session itself: a second
    /// counts as warm-up while its median exceeds <see cref="WarmupMedianFactor"/> × the median of
    /// all per-second medians. The scan stops at the first normal second — a stutter in the middle
    /// of a match is a defect to report, never warm-up to hide — and never exceeds
    /// <paramref name="cap"/>.
    ///
    /// Known ceiling: the reference median includes the warm-up seconds, so a capture that is MORE
    /// than half warm-up drags the reference up and under-detects. That is the safe direction — it
    /// keeps loading frames rather than deleting gameplay — and a capture that is majority loading
    /// screen is not a benchmark anyway.
    /// </summary>
    internal static int DetectWarmup(Dictionary<int, List<double>> bySecond, int cap)
    {
        if (cap <= 0 || bySecond.Count == 0) return 0;

        var seconds = bySecond.Keys.OrderBy(s => s).ToList();
        var medians = seconds.Select(s => Median(bySecond[s])).ToList();
        var reference = Median(medians.ToList());
        if (!(reference > 0)) return 0;

        var limit = reference * WarmupMedianFactor;
        var dropped = 0;
        while (dropped < medians.Count && dropped < cap && medians[dropped] > limit) dropped++;
        return dropped;
    }

    /// <summary>Is this slow second a CAP (regular by construction) rather than a struggling game?
    /// See <see cref="CapRegularityMax"/> for why regularity is the discriminator and where the
    /// 0.04 comes from. Too few frames to judge (under four) means no verdict — and no verdict
    /// means keep, because deleting on a guess is the error that hides a real slowdown.</summary>
    private static bool EstUnPlafond(List<double> bucket)
    {
        if (bucket.Count < 4) return false;

        var sorted = bucket.OrderBy(x => x).ToList();
        var mediane = Median(sorted);
        if (!(mediane > 0)) return false;

        var q1 = sorted[sorted.Count / 4];
        var q3 = sorted[3 * sorted.Count / 4];
        return (q3 - q1) / mediane < CapRegularityMax;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
