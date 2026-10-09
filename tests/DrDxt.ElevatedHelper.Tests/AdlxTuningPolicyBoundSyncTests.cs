using DrDxt.Application.Tweaks.Gpu;
using DrDxt.ElevatedHelper.Execution.Adlx;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// T2 — the ELEVATED helper enforces the GPU safety envelope itself for the AMD ADLX
// write seam (mirror of NvApiTuningPolicyBoundSyncTests).
//
// The helper cannot reference DrDxt.Application (it depends only on DrDxt.Core + the leaf
// DrDxt.GpuNative), so the conservative POWER-LIMIT band is MIRRORED in AdlxTuningMath.
// GpuSafeBounds is the single source of truth; these tests fail loudly if the mirror ever
// drifts, and pin the helper-side rail (the default-DENY gate every ADLX power-limit write
// goes through) at the band edges.
//
// NOTE — the UNDERVOLT op carries an absolute MaxFreq (MHz), not an offset, so there is no
// offset band to mirror here: the conservative OFFSET band [-200,+100] is enforced at the
// App layer (GpuSafeBounds) and the helper validates the absolute target against the DRIVER's
// own reported hardware range (asserted in ADLXTuningActionsTests, not here).
// -----------------------------------------------------------------------
public sealed class AdlxTuningPolicyBoundSyncTests
{
    // --- The mirror must equal GpuSafeBounds, leaf by leaf. ---

    [Fact]
    public void Power_limit_band_mirrors_GpuSafeBounds()
    {
        AdlxTuningMath.MinPowerLimitPct.Should().Be(GpuSafeBounds.MinPowerLimitPct);
        AdlxTuningMath.MaxPowerLimitPct.Should().Be(GpuSafeBounds.MaxPowerLimitPct);
    }

    // --- The helper-side rail enforces the same band (the default-DENY gate). ---

    [Theory]
    [InlineData(60, true)]             // floor — in band
    [InlineData(100, true)]            // ceiling — in band
    [InlineData(80, true)]
    [InlineData(59, false)]            // below the floor
    [InlineData(101, false)]           // above the ceiling
    [InlineData(int.MinValue, false)]
    [InlineData(int.MaxValue, false)]
    public void Power_limit_rail_enforces_the_policy_band(int pct, bool inBand) =>
        AdlxTuningMath.IsPowerLimitWithinHardRail(pct).Should().Be(inBand);
}
