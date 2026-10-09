using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Hardware;

public class BoardBiosDetectorTests
{
    private static IWmiQuery Wmi(string boardMfr, string boardProduct, string biosVer, string biosDate)
    {
        var q = Substitute.For<IWmiQuery>();
        q.Query(Arg.Is<string>(s => s.Contains("Win32_BaseBoard")))
         .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
         { new(new Dictionary<string, object?> { ["Manufacturer"] = boardMfr, ["Product"] = boardProduct }) }));
        q.Query(Arg.Is<string>(s => s.Contains("Win32_BIOS")))
         .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
         { new(new Dictionary<string, object?> { ["SMBIOSBIOSVersion"] = biosVer, ["ReleaseDate"] = biosDate }) }));
        return q;
    }

    [Fact]
    public void Reads_board_and_bios()
    {
        var fw = Substitute.For<IFirmwareProvider>();
        fw.IsUefi().Returns(true);
        var sut = new BoardBiosDetector(Wmi("MSI", "MAG B650 TOMAHAWK WIFI", "1.A0", "20240115000000.000000+000"), fw);

        var r = sut.Detect();
        r.IsOk.Should().BeTrue();
        r.Value!.Board.Manufacturer.Should().Be("MSI");
        r.Value.Board.Product.Should().Be("MAG B650 TOMAHAWK WIFI");
        r.Value.Bios.Version.Should().Be("1.A0");
        r.Value.Bios.IsUefi.Should().BeTrue();
        r.Value.Bios.ReleaseDate!.Value.Year.Should().Be(2024);
    }

    [Fact]
    public void Unparsable_bios_date_yields_null_not_throw()
    {
        var fw = Substitute.For<IFirmwareProvider>();
        var sut = new BoardBiosDetector(Wmi("ASUS", "TUF", "0601", "garbage"), fw);
        sut.Detect().Value!.Bios.ReleaseDate.Should().BeNull();
    }

    [Fact]
    public void Legacy_firmware_flagged_not_uefi()
    {
        var fw = Substitute.For<IFirmwareProvider>();
        fw.IsUefi().Returns(false);
        new BoardBiosDetector(Wmi("Gigabyte", "B550", "F16", "20210101000000.000000+000"), fw)
            .Detect().Value!.Bios.IsUefi.Should().BeFalse();
    }
}
