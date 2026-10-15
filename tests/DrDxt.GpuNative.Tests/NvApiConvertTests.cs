using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests.NvApi;

// -----------------------------------------------------------------------
// NvApiConvert : les conversions d'unités pures de la recette (MHz ↔ kHz, % ↔ PCM, le
// bornage pilote), placées ici avec la frontière native. Les bandes prudentes de décalage
// et de puissance restent dans le helper (NvApiTuningMath / politique) ; ces conversions
// relèvent de la recette et vivent dans la feuille à côté de NvApiNative.
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
        // Défensif : des bornes pilote aberrantes (max < min) ne doivent pas lever ; l'appelant échoue proprement.
        NvApiConvert.ClampPcm(90_000u, 120_000u, 50_000u).Should().Be(90_000u);
    }
}
