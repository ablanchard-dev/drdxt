using DrDxt.Core;
using DrDxt.Detection.Display;

namespace DrDxt.Detection.Tests.Display;

/// <summary>
/// Written from a real machine: the reference PC runs 1920x1080 @ 143 Hz with a 200 Hz
/// panel attached. DrDXT had no display probe at all, so its scan reported a healthy
/// machine while the single largest free win sat unflagged.
/// </summary>
public class DisplayRefreshHeuristicTests
{
    [Fact]
    public void Reports_headroom_when_the_panel_offers_more_at_the_same_resolution()
    {
        var r = DisplayRefreshHeuristic.Evaluate(143, new[] { 60, 120, 143, 165, 200 });

        Assert.Equal(TriState.On, r.HasHeadroom);
        Assert.Equal(143, r.CurrentHz);
        Assert.Equal(200, r.BestHz);
    }

    [Fact]
    public void Reports_no_headroom_when_already_on_the_best_mode()
    {
        var r = DisplayRefreshHeuristic.Evaluate(200, new[] { 60, 120, 200 });

        Assert.Equal(TriState.Off, r.HasHeadroom);
        Assert.Equal(200, r.BestHz);
    }

    [Fact]
    public void A_one_hertz_gap_is_the_same_mode_not_an_opportunity()
    {
        // Windows reports a 144 Hz mode as 143. Without the tolerance DrDXT would nag
        // about a display that is already maxed out.
        var r = DisplayRefreshHeuristic.Evaluate(143, new[] { 60, 144 });

        Assert.Equal(TriState.Off, r.HasHeadroom);
    }

    [Fact]
    public void Two_hertz_is_a_real_gap()
    {
        // Guards the boundary from the other side: the tolerance must not swallow
        // a genuine difference just above it.
        var r = DisplayRefreshHeuristic.Evaluate(143, new[] { 145 });

        Assert.Equal(TriState.On, r.HasHeadroom);
    }

    [Fact]
    public void An_empty_mode_list_is_unknown_not_fine()
    {
        // The failure that matters: enumeration produced nothing. Answering "Off"
        // would turn "I could not look" into "there is nothing better".
        var r = DisplayRefreshHeuristic.Evaluate(143, Array.Empty<int>());

        Assert.Equal(TriState.Unknown, r.HasHeadroom);
    }

    [Fact]
    public void A_null_mode_list_is_unknown()
    {
        var r = DisplayRefreshHeuristic.Evaluate(143, null);

        Assert.Equal(TriState.Unknown, r.HasHeadroom);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_unreadable_current_rate_is_unknown(int currentHz)
    {
        var r = DisplayRefreshHeuristic.Evaluate(currentHz, new[] { 60, 200 });

        Assert.Equal(TriState.Unknown, r.HasHeadroom);
    }

    [Fact]
    public void Never_reports_a_best_below_the_current_rate()
    {
        // A truncated or stale mode list must not make DrDXT claim the machine is
        // running ABOVE what the panel offers — that would read as a fault to fix.
        var r = DisplayRefreshHeuristic.Evaluate(200, new[] { 60, 120 });

        Assert.Equal(TriState.Off, r.HasHeadroom);
        Assert.Equal(200, r.BestHz);
    }
}
