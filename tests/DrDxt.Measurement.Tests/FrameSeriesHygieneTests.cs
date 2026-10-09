using FluentAssertions;

namespace DrDxt.Measurement.Tests;

public class FrameSeriesHygieneTests
{
    // n frames of `ms` each — one second of gameplay at 1000/ms fps.
    private static IEnumerable<double> Seconds(double seconds, double ms)
        => Enumerable.Repeat(ms, (int)Math.Round(seconds * 1000.0 / ms));

    [Fact]
    public void Drops_the_warmup_seconds()
    {
        // 10 s of loading-speed frames then 100 s of gameplay, warm-up cut at 10 s.
        var series = Seconds(10, 50).Concat(Seconds(100, 7)).ToList();

        var r = FrameSeriesHygiene.Clean(series, warmupSeconds: 10, outOfFocusMedianMs: 1000);

        r.Applied.Should().BeTrue();
        r.WarmupSecondsDropped.Should().Be(10);
        r.FrameTimesMs.Should().OnlyContain(x => x == 7);
    }

    [Fact]
    public void Drops_the_out_of_focus_seconds_by_their_median()
    {
        // 60 s of gameplay, then 20 s alt-tabbed at the 30 fps out-of-focus cap, then 60 s more.
        var series = Seconds(60, 7).Concat(Seconds(20, 33.3)).Concat(Seconds(60, 7)).ToList();

        var r = FrameSeriesHygiene.Clean(series, warmupSeconds: 0);

        r.Applied.Should().BeTrue();
        r.OutOfFocusSecondsDropped.Should().BeInRange(19, 21);
        // Only the two BOUNDARY seconds can survive: they hold a mix of gameplay and capped
        // frames, so their median stays under the threshold. Everything inside the alt-tab is
        // gone. Filtering by whole seconds cannot do better, and pretending otherwise would be
        // the lie this filter exists to avoid.
        r.FrameTimesMs.Count(x => x > 25).Should().BeLessThan(70);
        r.FrameTimesMs.Count(x => x == 7).Should().BeGreaterThan(16_000);
    }

    [Fact]
    public void A_single_stutter_does_not_delete_its_second()
    {
        // One 200 ms hitch inside an otherwise good second: the MEDIAN is still 7 ms, so the
        // second survives — the filter removes non-gameplay, never real stutters.
        var oneBadSecond = Seconds(1, 7).Concat(new[] { 200.0 });
        var series = Seconds(30, 7).Concat(oneBadSecond).Concat(Seconds(30, 7)).ToList();

        var r = FrameSeriesHygiene.Clean(series, warmupSeconds: 0);

        r.Applied.Should().BeTrue();
        r.OutOfFocusSecondsDropped.Should().Be(0);
        r.FrameTimesMs.Should().Contain(200.0);
    }

    [Fact]
    public void Refuses_rather_than_emptying_a_genuinely_slow_session()
    {
        // A machine really running at 30 fps: every second is above the threshold. Deleting the
        // whole capture would turn a real problem into an empty result.
        var series = Seconds(300, 33.3).ToList();

        var r = FrameSeriesHygiene.Clean(series, warmupSeconds: 0);

        r.Applied.Should().BeFalse();
        r.FrameTimesMs.Should().BeEquivalentTo(series);
    }

    [Fact]
    public void Slices_are_equal_duration_and_the_remainder_is_dropped()
    {
        var series = Seconds(100, 10).ToList(); // 100 s

        var slices = FrameSeriesHygiene.Slice(series, sliceSeconds: 30);

        slices.Should().HaveCount(3);                       // 90 s used, 10 s remainder dropped
        slices.Should().OnlyContain(s => s.Count == 3000);  // 30 s / 10 ms
    }

    [Fact]
    public void Slice_returns_nothing_when_the_session_is_shorter_than_one_slice()
        => FrameSeriesHygiene.Slice(Seconds(20, 10).ToList(), sliceSeconds: 30).Should().BeEmpty();

    [Fact]
    public void Slices_feed_the_inter_run_margin_from_a_single_session()
    {
        // The payoff: one long session yields the >=3 aggregates InterRunMarginOfError needs,
        // instead of demanding three separate benchmark runs.
        var series = Seconds(300, 10).ToList(); // 30 000 frames = exactly 5 x 60 s

        var perSlice = FrameSeriesHygiene.Slice(series, 60)
            .Select(s => FrametimeStatistics.AvgFps(s))
            .ToList();

        perSlice.Should().HaveCount(5);
        FrametimeStatistics.InterRunMarginOfError(perSlice).Should().NotBe(double.PositiveInfinity);
    }

    // --- warm-up is MEASURED, not assumed (regression: 2026-09-01 real capture) ---

    [Fact]
    public void Detects_no_warmup_when_the_capture_starts_in_gameplay()
    {
        // The real 178 s capture: every second already at gameplay pace. Dropping a flat 90 s
        // threw away half the frames for a 0.9 % change AND starved Slice of a single 120 s slice.
        var series = Seconds(178, 7.0).ToList();

        var r = FrameSeriesHygiene.Clean(series);

        r.Applied.Should().BeTrue();
        r.WarmupSecondsDropped.Should().Be(0);
        r.FrameTimesMs.Count.Should().Be(series.Count);
        FrameSeriesHygiene.Slice(r.FrameTimesMs, 120.0).Should().HaveCount(1,
            "178 s of kept gameplay yields one 120 s slice; a flat 90 s warm-up yielded none");
    }

    [Fact]
    public void Drops_a_real_loading_head_and_stops_at_the_first_playable_second()
    {
        // 12 s of loading at 30 fps, then gameplay at ~143 fps.
        var series = Seconds(12, 33.3).Concat(Seconds(100, 7.0)).ToList();

        var r = FrameSeriesHygiene.Clean(series);

        r.Applied.Should().BeTrue();
        r.WarmupSecondsDropped.Should().Be(12);
        r.FrameTimesMs.Should().OnlyContain(x => x == 7.0);
    }

    [Fact]
    public void A_stutter_after_the_start_is_never_swallowed_as_warmup()
    {
        // A defect to report, not warm-up to hide: the scan stops at the first normal second.
        var series = Seconds(30, 7.0).Concat(Seconds(3, 40.0)).Concat(Seconds(30, 7.0)).ToList();

        var r = FrameSeriesHygiene.Clean(series);

        r.WarmupSecondsDropped.Should().Be(0);
        r.OutOfFocusSecondsDropped.Should().Be(3, "40 ms seconds are still caught by the focus rule");
    }

    [Fact]
    public void Detected_warmup_never_exceeds_the_cap()
    {
        var series = Seconds(40, 33.3).Concat(Seconds(60, 7.0)).ToList();

        var r = FrameSeriesHygiene.Clean(series, warmupSeconds: 10.0);

        r.WarmupSecondsDropped.Should().Be(10, "the knob is an upper bound on the search");
    }
}
