using DrDxt.Application.Tweaks.Hardware;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Hardware;

// -----------------------------------------------------------------------
// "The panel is running below what it can do" — the first lever whose magnitude is
// arithmetic instead of a calibrated estimate.
//
// The case that produced it, on the author's own machine: a panel capable of 200 Hz driven
// at 144. The NVIDIA low-latency rule caps 3 under the refresh, so the only visible trace
// was a frame cap at 141. Fixing the cap would have fixed nothing.
// -----------------------------------------------------------------------
public sealed class DisplayCapabilityTests
{
    private static DisplayInfo Display(int current, int max) =>
        new(IsPrimaryGameDisplay: true, SupportsVrr: false, RefreshHz: current, MaxRefreshHz: max);

    [Fact]
    public void A_panel_driven_below_its_maximum_is_a_deficit_with_a_measured_size()
    {
        var d = Display(current: 144, max: 200);

        d.IsBelowCapability.Should().BeTrue();
        d.RefreshHeadroomHz.Should().Be(56);
    }

    [Fact]
    public void A_panel_at_its_maximum_is_not_a_deficit()
    {
        var d = Display(current: 200, max: 200);

        d.IsBelowCapability.Should().BeFalse();
        d.RefreshHeadroomHz.Should().Be(0);
    }

    [Fact]
    public void An_unread_maximum_never_becomes_a_deficit()
    {
        // 0 = "we did not read it". Treating that as a maximum of zero, or as equal to the
        // current rate, would either invent a deficit or hide one. It must do neither.
        var d = Display(current: 144, max: 0);

        d.IsBelowCapability.Should().BeFalse();
        d.RefreshHeadroomHz.Should().Be(0);
    }

    [Fact]
    public void An_unread_current_rate_never_becomes_a_deficit()
    {
        Display(current: 0, max: 200).IsBelowCapability.Should().BeFalse();
    }

    [Fact]
    public void A_maximum_below_the_current_rate_is_not_reported_as_headroom()
    {
        // Should not happen, but a negative headroom announced as a gain would be worse than
        // silence: the arithmetic must never produce a number the user can act on wrongly.
        var d = Display(current: 200, max: 144);

        d.IsBelowCapability.Should().BeFalse();
        d.RefreshHeadroomHz.Should().Be(0);
    }

    [Fact]
    public void The_default_construction_stays_source_compatible_and_reads_nothing()
    {
        // Existing three-argument constructions must keep compiling AND must not start
        // claiming a maximum they never read.
        var legacy = new DisplayInfo(IsPrimaryGameDisplay: true, SupportsVrr: true, RefreshHz: 144);

        legacy.MaxRefreshHz.Should().Be(0);
        legacy.IsBelowCapability.Should().BeFalse();
    }

    [Fact]
    public void The_faster_screen_on_a_two_screen_desk_is_visible()
    {
        // The whole reason GetAllDisplays exists: with one display in hand, nothing can tell
        // the user the game is running on the slower panel.
        var screens = new[]
        {
            new DisplayInfo(true, false, RefreshHz: 144, MaxRefreshHz: 200, DeviceName: @"\\.\DISPLAY1"),
            new DisplayInfo(false, false, RefreshHz: 165, MaxRefreshHz: 165, DeviceName: @"\\.\DISPLAY2"),
        };

        var fastest = screens.OrderByDescending(s => s.RefreshHz).First();
        fastest.IsPrimaryGameDisplay.Should().BeFalse("the game is on the slower screen here");

        screens.Count(s => s.IsBelowCapability).Should().Be(1);
        screens.Single(s => s.IsBelowCapability).RefreshHeadroomHz.Should().Be(56);
    }
}
