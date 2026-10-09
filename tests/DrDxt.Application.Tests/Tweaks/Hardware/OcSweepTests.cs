using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Hardware;

// -----------------------------------------------------------------------
// P2 — OcSweep: the pure ascending core-offset sweep decision logic. Measures stock,
// then each candidate offset on the deterministic perf-meter (P1); keeps the best
// PROVEN gain (GpuBenchAb: delta clears the combined margin AND stable); stops at the
// first unstable/failed step; always ends by applying the best offset (0 = stock when
// nothing proved better). Pure orchestration over two delegates — no GPU in tests.
// -----------------------------------------------------------------------
public sealed class OcSweepTests
{
    private static GpuBenchAggregate Agg(double mean, double margin = 1, bool stable = true)
        => new(mean, margin, 3, stable);

    // measure delegate keyed by the offset last applied.
    private static (Func<Result<GpuBenchAggregate>> Measure, Func<int, bool> Apply, List<int> Applied)
        Rig(Dictionary<int, Result<GpuBenchAggregate>> byOffset, Func<int, bool>? applyOverride = null)
    {
        var applied = new List<int>();
        var current = 0;
        bool Apply(int mhz)
        {
            if (applyOverride is not null && !applyOverride(mhz)) return false;
            current = mhz;
            applied.Add(mhz);
            return true;
        }
        Result<GpuBenchAggregate> Measure() =>
            byOffset.TryGetValue(current, out var r)
                ? r
                : Result<GpuBenchAggregate>.Fail(ErrorCodes.CaptureFail, $"no rig entry for {current}");
        return (Measure, Apply, applied);
    }

    [Fact]
    public void Keeps_the_highest_proven_gain_and_ends_applied_at_it()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100)),
            [30] = Result<GpuBenchAggregate>.Ok(Agg(104)),   // gain (margin 1+1=2)
            [60] = Result<GpuBenchAggregate>.Ok(Agg(108)),   // bigger gain
            [90] = Result<GpuBenchAggregate>.Ok(Agg(108.5)), // within noise vs stock? 8.5 > 2 → still gain, best score
        });

        var r = OcSweep.Run(measure, apply, new[] { 30, 60, 90 });

        r.IsOk.Should().BeTrue();
        r.Value!.BestOffsetMhz.Should().Be(90);
        r.Value.GainPct.Should().BeApproximately(8.5, 0.01);
        applied.Last().Should().Be(90); // ends applied at the best
        r.Value.EndedAtBest.Should().BeTrue();
    }

    [Fact]
    public void Unstable_candidate_stops_the_sweep_and_reverts_to_the_previous_best()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100)),
            [30] = Result<GpuBenchAggregate>.Ok(Agg(105)),                  // gain
            [60] = Result<GpuBenchAggregate>.Ok(Agg(120, stable: false)),  // TDR/checksum → stop
        });

        var r = OcSweep.Run(measure, apply, new[] { 30, 60, 90 });

        r.IsOk.Should().BeTrue();
        r.Value!.BestOffsetMhz.Should().Be(30);
        applied.Last().Should().Be(30);              // reverted to the proven best
        applied.Should().NotContain(90);             // never went past the instability
    }

    [Fact]
    public void No_proven_gain_ends_back_at_stock()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100, margin: 5)),
            [30] = Result<GpuBenchAggregate>.Ok(Agg(103, margin: 5)),  // within combined noise
        });

        var r = OcSweep.Run(measure, apply, new[] { 30 });

        r.Value!.BestOffsetMhz.Should().Be(0);
        r.Value.GainPct.Should().Be(0);
        applied.Last().Should().Be(0);
    }

    [Fact]
    public void Out_of_band_candidates_are_never_applied()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100)),
        });

        var r = OcSweep.Run(measure, apply, new[] { 150, 500 }); // > GpuSafeBounds.MaxCoreOffsetMhz (100)

        r.IsOk.Should().BeTrue();
        applied.Should().OnlyContain(o => o == 0);   // just the initial stock + final restore
        r.Value!.BestOffsetMhz.Should().Be(0);
    }

    [Fact]
    public void Memory_band_candidates_pass_when_the_memory_band_check_is_supplied()
    {
        // +300 is outside the core band (+100 cap) but inside the memory band (±500):
        // with the memory band check supplied it must be swept, not silently dropped.
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100)),
            [300] = Result<GpuBenchAggregate>.Ok(Agg(105)),
        });

        var r = OcSweep.Run(measure, apply, new[] { 300 },
            isInBand: DrDxt.Application.Tweaks.Gpu.GpuSafeBounds.IsMemoryOffsetSafe);

        r.IsOk.Should().BeTrue();
        r.Value!.BestOffsetMhz.Should().Be(300);
        applied.Should().Contain(300);
    }

    [Fact]
    public void Unstable_stock_fails_clean_without_applying_any_offset()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100, stable: false)),
        });

        var r = OcSweep.Run(measure, apply, new[] { 30 });

        r.IsOk.Should().BeFalse();
        applied.Should().OnlyContain(o => o == 0);
    }

    [Fact]
    public void Failed_apply_stops_and_restores_the_previous_best()
    {
        var (measure, apply, applied) = Rig(new()
        {
            [0] = Result<GpuBenchAggregate>.Ok(Agg(100)),
            [30] = Result<GpuBenchAggregate>.Ok(Agg(105)),
        }, applyOverride: mhz => mhz != 60); // +60 write fails

        var r = OcSweep.Run(measure, apply, new[] { 30, 60 });

        r.IsOk.Should().BeTrue();
        r.Value!.BestOffsetMhz.Should().Be(30);
        applied.Last().Should().Be(30);
    }
}
