using DrDxt.Detection.Hardware;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Hardware;

// The XMP/EXPO rated speed lives in the DRAM part number, not reliably in WMI.
// After a BIOS flash resets XMP, WMI Speed collapses to the JEDEC value
// (== ConfiguredClockSpeed), so the part number is the only honest source of the
// kit's rated speed. We parse it conservatively: a 4-digit data-rate in a plausible
// DDR4/DDR5 range, taking the highest match (the headline speed, never a timing code).
public class DramPartNumberTests
{
    [Fact]
    public void GSkill_ddr4_4000_kit_parses_to_4000()
    {
        // The reference machine's exact kit (G.Skill F4-4000C18-32GTES) — the regression that motivated this.
        DramPartNumber.TryParseRatedMhz("F4-4000C18-32GTES").Should().Be(4000);
    }

    [Fact]
    public void Corsair_ddr4_3200_parses_to_3200()
    {
        DramPartNumber.TryParseRatedMhz("CMK16GX4M2B3200C16").Should().Be(3200);
    }

    [Fact]
    public void Corsair_ddr5_6000_parses_to_6000()
    {
        DramPartNumber.TryParseRatedMhz("CMK32GX5M2B6000Z30").Should().Be(6000);
    }

    [Fact]
    public void GSkill_ddr5_part_picks_the_speed_not_the_timing_code()
    {
        // "F5-6000J3038..." contains TWO 4-digit runs: 6000 (speed) and 3038 (J-timing).
        // The headline speed is the higher one — proves we take the max, not the first.
        DramPartNumber.TryParseRatedMhz("F5-6000J3038F16GX2-TZ5RK").Should().Be(6000);
    }

    [Fact]
    public void Unparseable_part_number_returns_null_never_invents_a_speed()
    {
        DramPartNumber.TryParseRatedMhz("Unknown").Should().BeNull();
    }

    [Fact]
    public void Null_or_empty_returns_null()
    {
        DramPartNumber.TryParseRatedMhz(null).Should().BeNull();
        DramPartNumber.TryParseRatedMhz("").Should().BeNull();
        DramPartNumber.TryParseRatedMhz("   ").Should().BeNull();
    }

    [Fact]
    public void Four_digit_numbers_outside_the_dram_range_are_ignored()
    {
        // 1999 (below DDR4 floor) and 9500 (above ceiling) are not plausible data-rates.
        DramPartNumber.TryParseRatedMhz("ABCD1999EFGH").Should().BeNull();
        DramPartNumber.TryParseRatedMhz("XYZ9500QRS").Should().BeNull();
    }

    // ── 26/08 : le tCL, seule source possible d'un timing sans pilote noyau ──

    [Theory]
    [InlineData("F4-4000C18-32GTES", 18)]         // le kit de la machine de reference (mesure)
    [InlineData("F4-3600C16D-32GTZN", 16)]
    [InlineData("F5-6000J3038F16GX2-TZ5RK", 30)]  // DDR5 : J<CL><tRCD><tRP>
    public void Parses_the_CAS_latency_from_the_part_number(string pn, int expected)
    {
        DramPartNumber.TryParseCasLatency(pn).Should().Be(expected);
    }

    [Theory]
    [InlineData("KF432C16BB/16")]   // Kingston : pas la forme <rate>C<CL>
    [InlineData("")]
    [InlineData(null)]
    [InlineData("CT16G4DFRA32A")]   // Crucial : aucun CL encode
    public void Returns_null_rather_than_guessing_a_CAS_latency(string? pn)
    {
        // Un CL invente produirait une latence inventee. Mieux vaut ne rien rendre.
        DramPartNumber.TryParseCasLatency(pn).Should().BeNull();
    }
}
