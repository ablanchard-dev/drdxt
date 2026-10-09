using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Hardware;

public class MemoryDetectorTests
{
    private static IWmiQuery Wmi(params (string pn, string mfr, int cfg, int rated)[] mods)
    {
        var q = Substitute.For<IWmiQuery>();
        var rows = mods.Select(m => new WmiRow(new Dictionary<string, object?>
        {
            ["PartNumber"] = m.pn, ["Manufacturer"] = m.mfr,
            ["ConfiguredClockSpeed"] = (uint)m.cfg, ["Speed"] = (uint)m.rated
        })).ToList();
        q.Query(Arg.Is<string>(s => s.Contains("Win32_PhysicalMemory")))
         .Returns(Result<IReadOnlyList<WmiRow>>.Ok(rows));
        return q;
    }

    [Fact]
    public void Maps_part_number_and_speeds()
    {
        var sut = new MemoryDetector(Wmi(("CMK32GX5M2B6000Z30", "Corsair", 4800, 6000)));
        var m = sut.Detect().Value!.Single();
        m.PartNumber.Should().Be("CMK32GX5M2B6000Z30");
        m.Manufacturer.Should().Be("Corsair");
        m.ConfiguredMhz.Should().Be(4800);
        m.RatedMhz.Should().Be(6000);
    }

    [Fact]
    public void Multiple_modules_all_returned()
    {
        var sut = new MemoryDetector(Wmi(
            ("A", "Corsair", 4800, 6000), ("A", "Corsair", 4800, 6000)));
        sut.Detect().Value!.Should().HaveCount(2);
    }

    [Fact]
    public void Missing_speeds_default_to_zero_not_throw()
    {
        var q = Substitute.For<IWmiQuery>();
        q.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Ok(
            new List<WmiRow> { new(new Dictionary<string, object?> { ["PartNumber"] = "X" }) }));
        var m = new MemoryDetector(q).Detect().Value!.Single();
        m.ConfiguredMhz.Should().Be(0);
        m.RatedMhz.Should().Be(0);
    }

    [Fact]
    public void Wmi_failure_propagates()
    {
        var q = Substitute.For<IWmiQuery>();
        q.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "x"));
        new MemoryDetector(q).Detect().IsOk.Should().BeFalse();
    }

    [Fact]
    public void Rated_speed_comes_from_part_number_when_wmi_speed_collapsed_to_configured()
    {
        // Real post-BIOS-flash state on the reference machine: XMP reset, so WMI reports
        // Speed == ConfiguredClockSpeed == 2667 (JEDEC). The 4000 rating only exists
        // in the part number. RatedMhz must recover it so the regression is detectable.
        var sut = new MemoryDetector(Wmi(("F4-4000C18-32GTES", "G Skill Intl", 2667, 2667)));
        sut.Detect().Value!.Single().RatedMhz.Should().Be(4000);
    }

    [Fact]
    public void Detected_module_with_collapsed_wmi_speed_still_infers_xmp_off()
    {
        // End-to-end guard: detect → infer. The whole point — DrDXT must catch
        // "XMP got reset by a BIOS update" even when WMI hides the rated speed.
        var sut = new MemoryDetector(Wmi(("F4-4000C18-32GTES", "G Skill Intl", 2667, 2667)));
        var modules = sut.Detect().Value!;
        XmpInferencer.Infer(modules).Should().Be(TriState.Off);
    }

    [Fact]
    public void Wmi_speed_is_kept_when_it_already_reports_the_rated_speed()
    {
        // Some boards report the rated speed in WMI Speed (here 6000 > the 6000 in the
        // part number). max(wmi, parsed) must not regress that case.
        var sut = new MemoryDetector(Wmi(("CMK32GX5M2B6000Z30", "Corsair", 4800, 6000)));
        sut.Detect().Value!.Single().RatedMhz.Should().Be(6000);
    }

    [Fact]
    public void Unparseable_part_number_falls_back_to_wmi_speed()
    {
        // Kingston Fury's compact encoding (KF4 32 = DDR4-3200) has no 4-digit run we
        // parse → we must not invent a speed; keep the WMI value honestly.
        var sut = new MemoryDetector(Wmi(("KF432C16BB1A", "Kingston", 3200, 3200)));
        sut.Detect().Value!.Single().RatedMhz.Should().Be(3200);
    }
}
