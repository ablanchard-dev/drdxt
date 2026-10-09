using DrDxt.Core;

namespace DrDxt.App;

#if DEBUG // QA-only fabricated sample — compiled OUT of Release so these numbers can never ship.

/// <summary>
/// QA-only sample data for screenshotting the dashboard via the <c>dashboard-preview</c>
/// startup arg. Not shipped product logic — it fabricates a representative measurable-gain
/// <see cref="ApplyReport"/> (a couple of applied tweaks, before/after frametime series, 1%
/// /0.1% lows and an estimated PC latency) so the View can be rendered without running the
/// real apply pipeline. Numbers are illustrative, not measured.
/// </summary>
public static class DashboardSample
{
    /// <summary>A measurable-gain report: frame times drop, lows rise, latency improves,
    /// one applied-and-kept tweak plus one auto-reverted tweak so both list states show.</summary>
    public static ApplyReport MeasurableGain()
    {
        var before = new BenchmarkResult(
            AvgFps: 198,
            P1LowFps: 142,
            P01LowFps: 108,
            FrameTimesMs: Wave(count: 60, baseMs: 5.05, jitter: 0.9, seed: 7),
            EstimatedPcLatencyMs: 19.4,
            MarginOfError: 2.1,
            Runs: 5,
            MeanGpuBusyMs: 4.2); // ≈ 83% of the 5.05ms frame

        var after = new BenchmarkResult(
            AvgFps: 231,
            P1LowFps: 188,
            P01LowFps: 151,
            FrameTimesMs: Wave(count: 60, baseMs: 4.33, jitter: 0.4, seed: 13),
            EstimatedPcLatencyMs: 13.1,
            MarginOfError: 1.8,
            Runs: 5,
            MeanGpuBusyMs: 4.1); // ≈ 95% of the 4.33ms frame (more GPU-bound after)

        var verdict = new AbVerdict(before, after, IsMeasurableGain: true, "verdict.measurable_gain");

        var outcomes = new[]
        {
            new ApplyOutcome("latency.reflex", Applied: true, AutoReverted: false,
                new TweakSnapshot("latency.reflex", "{}"), Error: null),
            new ApplyOutcome("fixit.power_plan", Applied: true, AutoReverted: false,
                new TweakSnapshot("fixit.power_plan", "{}"), Error: null),
            // One tweak that the engine measured as a regression and rolled back, so the
            // auto-reverted section is visible in the screenshot.
            new ApplyOutcome("gpu.undervolt", Applied: true, AutoReverted: true,
                new TweakSnapshot("gpu.undervolt", "{}"), Error: null),
        };

        return new ApplyReport(outcomes, verdict);
    }

    // Deterministic pseudo-frametime wave so screenshots are stable run-to-run.
    private static IReadOnlyList<double> Wave(int count, double baseMs, double jitter, int seed)
    {
        var rng = new Random(seed);
        var list = new List<double>(count);
        for (int i = 0; i < count; i++)
        {
            double oscill = Math.Sin(i / 3.0) * jitter * 0.6;
            double noise = (rng.NextDouble() - 0.5) * jitter;
            list.Add(Math.Round(baseMs + oscill + noise, 3));
        }
        return list;
    }
}

#endif
