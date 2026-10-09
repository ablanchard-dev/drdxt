using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests.NvApi;

// -----------------------------------------------------------------------
// NvApiConvert — the recipe's pure unit conversions (MHz↔kHz, %↔PCM, the driver
// clamp), relocated here with the native boundary by the 02f/N1 extraction. The
// conservative offset / power BANDS stay in the helper (NvApiTuningMath / policy);
// these conversions are recipe-level and live in the leaf next to NvApiNative. The
// assertions are unchanged from the original NvApiTuningMathTests.
// -----------------------------------------------------------------------
public sealed class NvApiConvertTests
{
    [Fact]
    public void Mhz_to_khz_multiplies_by_1000() =>
        NvApiConvert.MhzToKhz(100).Should().Be(100_000);

    [Fact]
    public void Negative_offset_mhz_to_khz_keeps_sign() =>
        NvApiConvert.MhzToKhz(-120).Should().Be(-120_000);

    [Fact]
    public void Khz_to_mhz_divides_by_1000_truncating() =>
        NvApiConvert.KhzToMhz(-120_000).Should().Be(-120);

    [Fact]
    public void Percent_to_pcm_multiplies_by_1000() =>
        NvApiConvert.PercentToPcm(90).Should().Be(90_000u);

    [Fact]
    public void Pcm_to_percent_divides_by_1000() =>
        NvApiConvert.PcmToPercent(90_000u).Should().Be(90);

    [Fact]
    public void Clamp_pcm_keeps_an_in_range_value()
    {
        NvApiConvert.ClampPcm(90_000u, 50_000u, 120_000u).Should().Be(90_000u);
    }

    [Fact]
    public void Clamp_pcm_raises_a_below_min_value_to_min()
    {
        NvApiConvert.ClampPcm(40_000u, 50_000u, 120_000u).Should().Be(50_000u);
    }

    [Fact]
    public void Clamp_pcm_lowers_an_above_max_value_to_max()
    {
        NvApiConvert.ClampPcm(200_000u, 50_000u, 120_000u).Should().Be(120_000u);
    }

    [Fact]
    public void Clamp_pcm_with_inverted_bounds_returns_value_unchanged()
    {
        // Defensive: bad driver bounds (max < min) must not throw — caller fails clean.
        NvApiConvert.ClampPcm(90_000u, 120_000u, 50_000u).Should().Be(90_000u);
    }
}
