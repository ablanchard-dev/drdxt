using DrDxt.Detection.Hardware;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Hardware;

// La vitesse nominale XMP/EXPO vit dans la référence de la barrette, pas de façon fiable dans WMI.
// Après un flash du BIOS qui remet XMP à zéro, WMI Speed retombe à la valeur JEDEC
// (== ConfiguredClockSpeed), donc la référence est la seule source honnête de la vitesse
// nominale du kit. On l'analyse avec prudence : un débit à 4 chiffres dans une plage DDR4/DDR5
// plausible, en prenant la plus haute correspondance (la vitesse affichée, jamais un code de timing).
public class DramPartNumberTests
{
    [Fact]
    public void GSkill_ddr4_4000_kit_parses_to_4000()
    {
        // Le kit exact de la machine de référence (G.Skill F4-4000C18-32GTES) : la régression à l'origine de ceci.
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
        // « F5-6000J3038... » contient deux suites de 4 chiffres : 6000 (vitesse) et 3038 (timing J).
        // La vitesse affichée est la plus haute : prouve qu'on prend le max, pas la première.
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

    // Crucial code sa vitesse autrement : CT16G4DFD8266 est une DDR4-2666, et « 8266 » se lirait 8 266 MHz. Le débit
    // réglé (2666) passerait sous le socle : « XMP éteint », constat majeur, sur une barrette sans profil. Comme Kingston,
    // une référence Crucial ne donne aucun débit ; l'appelant garde la valeur WMI.
    [Theory]
    [InlineData("CT16G4DFD8266")]
    [InlineData("CT8G4DFS8266.C8FE")]
    [InlineData("CT16G4DFRA32A")]
    public void Crucial_ne_donne_jamais_un_debit_devine(string pn)
        => DramPartNumber.TryParseRatedMhz(pn).Should().BeNull();

    [Fact]
    public void Four_digit_numbers_outside_the_dram_range_are_ignored()
    {
        // 1999 (sous le plancher DDR4) et 9500 (au-dessus du plafond) ne sont pas des débits plausibles.
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
