using DrDxt.Core;
using DrDxt.Measurement;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// P1 — the deterministic GPU perf-METER. GpuBench turns ONE run into a comparable
// throughput score; this runs it N times and aggregates the scores into a mean + an
// inter-run margin, so a stock-vs-offset A/B has a real error bar (never a single-run
// guess). Because the workload is identical run-to-run, the only thing that moves the
// score is GPU clock/throughput — no map/scene noise (the reason we can't trust raw
// in-game for GPU levers). The margin reuses the same NS-6 statistic as the frametime
// A/B (FrametimeStatistics.InterRunMarginOfError: +Infinity under 3 runs = "not
// provable"), so the two proof paths stay consistent.
// -----------------------------------------------------------------------

/// <summary>Aggregate of N deterministic bench runs: the mean throughput SCORE, its 95%
/// inter-run margin (across the stable runs; +Infinity under 3), how many runs were stable,
/// and whether every run was stable (checksum-ok, no TDR). An aggregate with AllStable=false
/// is not trustworthy for an A/B — an unstable candidate (offset) must be reverted.</summary>
public sealed record GpuBenchAggregate(double MeanScore, double MarginOfError, int StableRuns, bool AllStable);

public sealed class GpuBenchSession
{
    private readonly GpuBench _bench;

    public GpuBenchSession(GpuBench bench) => _bench = bench;

    /// <summary>Runs the deterministic bench <paramref name="runs"/> times (>=1). Collects the
    /// throughput score of every STABLE run; any non-stable / inconclusive run sets AllStable to
    /// false and is excluded from the score. The margin needs ≥3 stable scores (else +Infinity —
    /// a gain can never be claimed on fewer). Fails clean only when NO run could execute.</summary>
    public Result<GpuBenchAggregate> Measure(int gpuIndex, TimeSpan perRun, int runs, CancellationToken ct)
    {
        if (runs < 1)
            return Result<GpuBenchAggregate>.Fail(ErrorCodes.CaptureFail, "runs must be >= 1.");

        var scores = new List<double>(runs);
        var allStable = true;

        for (var i = 0; i < runs; i++)
        {
            var r = _bench.Run(gpuIndex, perRun, ct);
            if (!r.IsOk || r.Value is null || r.Value.Stability != StabilityOutcome.Stable)
            {
                allStable = false; // TDR / checksum-mismatch / could-not-run — never counts as a good score
                continue;
            }
            scores.Add(r.Value.ElementIterationsPerSecond);
        }

        if (scores.Count == 0)
            return Result<GpuBenchAggregate>.Fail(ErrorCodes.CaptureFail,
                "No stable bench run: GPU unavailable, or the applied setting is unstable.");

        var mean = scores.Average();
        var margin = FrametimeStatistics.InterRunMarginOfError(scores); // +inf with < 3 stable runs
        return Result<GpuBenchAggregate>.Ok(new GpuBenchAggregate(mean, margin, scores.Count, allStable));
    }
}

/// <summary>The clean stock-vs-candidate verdict for a GPU-tuning A/B.</summary>
public enum GpuBenchAbVerdict
{
    /// <summary>Candidate score beats stock BEYOND the combined inter-run margin.</summary>
    Gain,
    /// <summary>Candidate score is below stock beyond the combined margin.</summary>
    Regression,
    /// <summary>The score moved within the combined margin — not a provable change.</summary>
    NoMeasurableChange,
    /// <summary>Either side had an unstable run — the candidate must be reverted, never kept.</summary>
    Unstable,
}

/// <summary>Pure A/B verdict between two <see cref="GpuBenchAggregate"/>. A gain REQUIRES the
/// score delta to clear the combined margin (never a within-noise claim) AND both sides fully
/// stable — an unstable candidate is always <see cref="GpuBenchAbVerdict.Unstable"/> (revert).</summary>
public static class GpuBenchAb
{
    public static GpuBenchAbVerdict Compare(GpuBenchAggregate stock, GpuBenchAggregate candidate)
    {
        if (!stock.AllStable || !candidate.AllStable)
            return GpuBenchAbVerdict.Unstable;

        var combinedMargin = stock.MarginOfError + candidate.MarginOfError;
        var delta = candidate.MeanScore - stock.MeanScore;

        if (delta > combinedMargin) return GpuBenchAbVerdict.Gain;
        if (delta < -combinedMargin) return GpuBenchAbVerdict.Regression;
        return GpuBenchAbVerdict.NoMeasurableChange;
    }
}
