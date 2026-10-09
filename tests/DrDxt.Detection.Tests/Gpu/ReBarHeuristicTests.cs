using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Gpu;

public class ReBarHeuristicTests
{
    private const ulong Mib = 1024UL * 1024UL;
    private const ulong Gib = 1024UL * Mib;

    [Fact]
    public void Bar1_covering_full_framebuffer_is_on()
        => ReBarHeuristic.FromBar1AndFb(8192 * Mib, 8188 * Mib).Should().Be(TriState.On);

    [Fact]
    public void Legacy_256mib_window_is_off()
        => ReBarHeuristic.FromBar1AndFb(256 * Mib, 8 * Gib).Should().Be(TriState.Off);

    [Fact]
    public void Bar1_at_exactly_half_framebuffer_is_on()
        => ReBarHeuristic.FromBar1AndFb(4 * Gib, 8 * Gib).Should().Be(TriState.On);

    [Fact]
    public void Zero_bar1_is_unknown()
        => ReBarHeuristic.FromBar1AndFb(0, 8 * Gib).Should().Be(TriState.Unknown);

    [Fact]
    public void Zero_framebuffer_is_unknown()
        => ReBarHeuristic.FromBar1AndFb(8 * Gib, 0).Should().Be(TriState.Unknown);
}
