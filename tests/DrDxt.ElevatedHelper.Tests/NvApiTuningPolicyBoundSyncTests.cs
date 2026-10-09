using DrDxt.Application.Tweaks.Gpu;
using DrDxt.ElevatedHelper.Execution.NvApi;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// M1 — the ELEVATED helper enforces the GPU safety envelope itself.
//
// The helper cannot reference DrDxt.Application (it depends only on DrDxt.Core), so the
// conservative band is MIRRORED in NvApiTuningMath. GpuSafeBounds is the single source of
// TRUTH; these tests fail loudly if the mirror ever drifts, and pin the tightened executor
// rail at the band edges (the rail the direct, non-IPC smoke-tool write path goes through).
// -----------------------------------------------------------------------
public sealed class NvApiTuningPolicyBoundSyncTests
{
    // --- The mirror must equal GpuSafeBounds, leaf by leaf. ---

    [Fact]
    public void Core_offset_band_mirrors_GpuSafeBounds()
    {
        NvApiTuningMath.MinCoreOffsetMhz.Should().Be(GpuSafeBounds.MinCoreOffsetMhz);
        NvApiTuningMath.MaxCoreOffsetMhz.Should().Be(GpuSafeBounds.MaxCoreOffsetMhz);
    }

    [Fact]
    public void Memory_offset_band_mirrors_GpuSafeBounds()
    {
        NvApiTuningMath.MinMemoryOffsetMhz.Should().Be(GpuSafeBounds.MinMemoryOffsetMhz);
        NvApiTuningMath.MaxMemoryOffsetMhz.Should().Be(GpuSafeBounds.MaxMemoryOffsetMhz);
    }

    // --- The tightened executor rail enforces the same band (this is the path a direct,
    //     non-IPC smoke-tool call to NvApiTuningActions.SetCoreClockOffset goes through). ---

    [Theory]
    [InlineData(100, true)]      // upper bound — in band
    [InlineData(-200, true)]     // lower bound — in band
    [InlineData(0, true)]
    [InlineData(101, false)]     // one past top
    [InlineData(-201, false)]    // one past bottom
    [InlineData(500, false)]     // the OLD ±500 rail — now rejected
    [InlineData(int.MinValue, false)]  // no Math.Abs overflow
    [InlineData(int.MaxValue, false)]
    public void Core_offset_rail_enforces_the_policy_band(int mhz, bool inBand) =>
        NvApiTuningMath.IsCoreOffsetWithinHardRail(mhz).Should().Be(inBand);

    [Theory]
    [InlineData(500, true)]
    [InlineData(-500, true)]
    [InlineData(0, true)]
    [InlineData(501, false)]
    [InlineData(-501, false)]
    [InlineData(int.MinValue, false)]
    public void Memory_offset_rail_enforces_the_policy_band(int mhz, bool inBand) =>
        NvApiTuningMath.IsMemoryOffsetWithinHardRail(mhz).Should().Be(inBand);
}
