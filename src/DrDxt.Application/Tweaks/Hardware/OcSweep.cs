using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// P2 — OcSweep: ascending core-offset sweep on the deterministic perf-meter (P1).
//
// Protocol (surgical, honest):
//   1. apply 0 (stock) and measure the stock aggregate — an UNSTABLE stock fails the
//      whole sweep (the machine is not measurable; nothing is ever applied),
//   2. for each candidate (ascending, in-band per GpuSafeBounds, out-of-band silently
//      skipped so the sweep can never write past the policy): apply → measure →
//      GpuBenchAb verdict vs stock,
//   3. keep the best PROVEN gain (delta clears the combined inter-run margin AND every
//      run stable); the first unstable / failed step STOPS the sweep,
//   4. Always finish by re-applying the best offset (0 = stock when nothing proved
//      better) — never leaves an unproven offset on the GPU, even on early exit.
//
// Pure orchestration over two delegates (measure / apply) so the decision logic is
// fully unit-tested with no GPU; the octune harness closes them over GpuBenchSession
// and the live NVAPI write.
// -----------------------------------------------------------------------

/// <summary>One sweep step: the offset tried, whether the write took, the measured
/// aggregate (null when the apply or the measure failed) and its verdict vs stock.</summary>
public sealed record OcSweepStep(int OffsetMhz, bool ApplyOk, GpuBenchAggregate? Aggregate, GpuBenchAbVerdict? Verdict);

/// <summary>Sweep outcome: the best proven offset (0 = stock), its gain % vs stock
/// (0 when stock), the stock aggregate, every step, and whether the final restore-to-best
/// write succeeded (false = the GPU may not be at BestOffsetMhz — surface loudly).</summary>
public sealed record OcSweepOutcome(
    int BestOffsetMhz,
    double GainPct,
    GpuBenchAggregate Stock,
    IReadOnlyList<OcSweepStep> Steps,
    bool EndedAtBest);

public static class OcSweep
{
    public static Result<OcSweepOutcome> Run(
        Func<Result<GpuBenchAggregate>> measure,
        Func<int, bool> applyOffset,
        IReadOnlyList<int> candidateOffsetsMhz,
        // The clock domain decides the safe band: core by default; pass
        // GpuSafeBounds.IsMemoryOffsetSafe for a memory-offset sweep (±500).
        Func<int, bool>? isInBand = null)
    {
        isInBand ??= GpuSafeBounds.IsCoreOffsetSafe;

        // In-band, non-stock, ascending, deduped. Out-of-band candidates are dropped
        // here so a caller mistake can never reach the GPU (defense before the write).
        var candidates = candidateOffsetsMhz
            .Where(o => o != 0 && isInBand(o))
            .Distinct()
            .OrderBy(o => o)
            .ToList();

        if (!applyOffset(0))
            return Result<OcSweepOutcome>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not set the stock offset.");

        var stockRead = measure();
        if (!stockRead.IsOk || stockRead.Value is null)
            return Result<OcSweepOutcome>.Fail(stockRead.Error?.Code ?? ErrorCodes.CaptureFail,
                $"Stock measurement failed: {stockRead.Error?.Message}");
        var stock = stockRead.Value;
        if (!stock.AllStable)
            return Result<OcSweepOutcome>.Fail(ErrorCodes.CaptureFail,
                "Stock run is not stable: the machine is not measurable; no offset was applied.");

        var steps = new List<OcSweepStep>(candidates.Count);
        var bestOffset = 0;
        var bestScore = stock.MeanScore;

        foreach (var offset in candidates)
        {
            if (!applyOffset(offset))
            {
                steps.Add(new OcSweepStep(offset, ApplyOk: false, null, null));
                break; // a refused write ends the sweep — restore below
            }

            var read = measure();
            if (!read.IsOk || read.Value is null)
            {
                steps.Add(new OcSweepStep(offset, ApplyOk: true, null, null));
                break;
            }

            var verdict = GpuBenchAb.Compare(stock, read.Value);
            steps.Add(new OcSweepStep(offset, ApplyOk: true, read.Value, verdict));

            if (verdict == GpuBenchAbVerdict.Unstable)
                break; // never push past the first instability

            if (verdict == GpuBenchAbVerdict.Gain && read.Value.MeanScore > bestScore)
            {
                bestOffset = offset;
                bestScore = read.Value.MeanScore;
            }
        }

        // Surgical exit: the GPU always ends at the best PROVEN offset (stock if none).
        var endedAtBest = applyOffset(bestOffset);
        var gainPct = bestOffset == 0 ? 0 : (bestScore - stock.MeanScore) / stock.MeanScore * 100.0;

        return Result<OcSweepOutcome>.Ok(new OcSweepOutcome(bestOffset, gainPct, stock, steps, endedAtBest));
    }
}
