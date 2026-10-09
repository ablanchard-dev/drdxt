using DrDxt.Core;
using DrDxt.Measurement;
using FluentAssertions;
using Xunit;

namespace DrDxt.Measurement.Tests;

public class AbComparerTests
{
    // avg + 1% low move together (avg*0.7) — the simple "everything up/down" cases.
    private static BenchmarkResult Bench(double avg, double moe) =>
        new(avg, avg * 0.7, avg * 0.5, new List<double>(), null, moe, 5, P1LowMarginOfError: moe);

    // Explicit avg / 1%-low / margin so we can vary frame-consistency INDEPENDENTLY
    // of average FPS — the whole point of the efficiency-tweak (undervolt) criterion.
    private static BenchmarkResult Bench(double avg, double p1Low, double moe) =>
        new(avg, p1Low, avg * 0.5, new List<double>(), null, moe, 5, P1LowMarginOfError: moe);

    // 26/09/2026 : les lows se jugeaient avec la marge des FPS moyens. Ici ils bougent de 6 FPS, au-dela
    // de la marge des moyennes (2 + 2) mais dans la leur (5 + 5) : ce n'est ni un gain ni une regression.
    [Fact]
    public void Les_lows_se_jugent_avec_leur_propre_marge_pas_celle_des_moyennes()
    {
        static BenchmarkResult B(double p1) => new(100, p1, 50, new List<double>(), null, 2, 5, P1LowMarginOfError: 5);

        AbComparer.Compare(B(70), B(76)).IsMeasurableGain.Should().BeFalse();
        AbComparer.Compare(B(76), B(70)).SummaryKey.Should().Be("Scan.Ab.NoMeasurableGain");
        AbComparer.Compare(B(70), B(81)).IsMeasurableGain.Should().BeTrue("temoin : au-dela de leur marge, c'est un gain");
    }

    [Fact]
    public void Gain_when_delta_exceeds_combined_margins()
    {
        var before = Bench(100, 1);
        var after = Bench(110, 1); // delta 10 > marges combinées (1+1=2)
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeTrue();
        v.SummaryKey.Should().Be("Scan.Ab.MeasurableGain");
    }

    [Fact]
    public void No_gain_when_delta_inside_combined_margins()
    {
        var before = Bench(100, 3);
        var after = Bench(103, 3); // delta 3 < marges combinées (6)
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.NoMeasurableGain");
    }

    [Fact]
    public void Regression_is_not_a_gain()
    {
        var v = AbComparer.Compare(Bench(110, 1), Bench(100, 1));
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.Regression");
    }

    [Fact]
    public void Verdict_carries_both_benchmarks()
    {
        var before = Bench(100, 1); var after = Bench(110, 1);
        var v = AbComparer.Compare(before, after);
        v.Before.Should().BeSameAs(before);
        v.After.Should().BeSameAs(after);
    }

    // --- Frame-consistency (1% lows) is the PRIMARY criterion (plan C2 / cahier §4) ---
    // An efficiency tweak (GPU undervolt / power-limit / fan curve) is ~0 on AVG FPS
    // by design — its value is steadier 1% lows + thermals. So the verdict must not be
    // average-FPS-only.

    [Fact]
    public void Gain_when_one_percent_lows_improve_beyond_margin_even_if_avg_flat()
    {
        // avg flat within margin, but 1% lows clearly up → the undervolt WIN: kept, gain.
        var before = Bench(avg: 100, p1Low: 70, moe: 1);
        var after = Bench(avg: 100, p1Low: 80, moe: 1); // avg delta 0; 1%-low delta 10 > 2
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeTrue();
        v.SummaryKey.Should().Be("Scan.Ab.MeasurableGain");
    }

    [Fact]
    public void No_gain_when_avg_and_one_percent_lows_both_within_margin()
    {
        // The expected good outcome of an efficiency tweak: avg held, 1% lows held.
        // Not a regression → must be KEPT (no auto-revert).
        var before = Bench(avg: 100, p1Low: 70, moe: 2);
        var after = Bench(avg: 101, p1Low: 71, moe: 2); // both deltas (1) < combined margin (4)
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.NoMeasurableGain");
    }

    [Fact]
    public void Regression_when_one_percent_lows_worse_beyond_margin_even_if_avg_flat()
    {
        // avg flat, but frame consistency degraded beyond margin → REGRESSION (auto-revert).
        // This is the per-game ReBAR/undervolt 1%-low regression the avg-only rule missed.
        var before = Bench(avg: 100, p1Low: 80, moe: 1);
        var after = Bench(avg: 100, p1Low: 70, moe: 1); // avg delta 0; 1%-low delta -10
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.Regression");
    }

    [Fact]
    public void Regression_when_avg_clearly_worse_beyond_margin()
    {
        // avg FPS clearly worse (1% lows steady) → still a regression on the avg signal.
        var before = Bench(avg: 110, p1Low: 80, moe: 1);
        var after = Bench(avg: 100, p1Low: 80, moe: 1); // avg delta -10; 1%-low flat
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.Regression");
    }

    [Fact]
    public void Regression_takes_priority_when_lows_drop_even_if_avg_rises_within_margin()
    {
        // avg nudges up within margin, but 1% lows collapse beyond margin → the frame
        // consistency loss wins: REGRESSION, never sold as a gain.
        var before = Bench(avg: 100, p1Low: 80, moe: 1);
        var after = Bench(avg: 101, p1Low: 70, moe: 1); // avg +1 (in margin); 1%-low -10
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.Regression");
    }

    // --- M8: the verdict carries the BASELINE limiter so the reco layer never sells a GPU tweak
    // as an FPS win on a CPU/other-bound machine. Classified from Before (the unmodified machine).
    [Fact]
    public void Verdict_reports_the_baseline_bottleneck()
    {
        // Before was CPU/other-bound (70 % GPU busy); After being GPU-bound must not change the
        // verdict's limiter — it's the machine's natural limiter that gates the claim.
        var before = new BenchmarkResult(100, 70, 50, new List<double>(), null, 1, 5, MeanGpuBusyMs: 7.0);
        var after = new BenchmarkResult(110, 77, 55, new List<double>(), null, 1, 5, MeanGpuBusyMs: 9.5);
        AbComparer.Compare(before, after).Bottleneck.Should().Be(Bottleneck.CpuOrOtherBound);
    }
}
