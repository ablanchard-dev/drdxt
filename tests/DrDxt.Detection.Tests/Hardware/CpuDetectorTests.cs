using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Hardware;

public class CpuDetectorTests
{
    private static IWmiQuery WmiReturning(params (string name, string vendor, int cores, int threads)[] cpus)
    {
        var q = Substitute.For<IWmiQuery>();
        var rows = cpus.Select(c => new WmiRow(new Dictionary<string, object?>
        {
            ["Name"] = c.name, ["Manufacturer"] = c.vendor,
            ["NumberOfCores"] = c.cores, ["NumberOfLogicalProcessors"] = c.threads
        })).ToList();
        q.Query(Arg.Is<string>(s => s.Contains("Win32_Processor")))
         .Returns(Result<IReadOnlyList<WmiRow>>.Ok(rows));
        return q;
    }

    [Fact]
    public void Detects_model_cores_threads()
    {
        var sut = new CpuDetector(WmiReturning(("AMD Ryzen 7 7800X3D", "AuthenticAMD", 8, 16)));
        var r = sut.Detect();
        r.IsOk.Should().BeTrue();
        r.Value!.Model.Should().Be("AMD Ryzen 7 7800X3D");
        r.Value.Cores.Should().Be(8);
        r.Value.Threads.Should().Be(16);
    }

    // VMware, un cœur par socket : huit lignes Win32_Processor à un cœur. Le rapport ne dit pas « 1c/1t ».
    [Fact]
    public void Les_coeurs_de_chaque_socket_se_somment()
    {
        var huit = Enumerable.Repeat(("Intel(R) Core(TM) i7-14700F", "GenuineIntel", 1, 1), 8).ToArray();
        var r = new CpuDetector(WmiReturning(huit)).Detect().Value!;
        r.Cores.Should().Be(8);
        r.Threads.Should().Be(8);
    }

    [Fact]
    public void Flags_x3d_from_model_name()
    {
        var sut = new CpuDetector(WmiReturning(("AMD Ryzen 7 7800X3D", "AuthenticAMD", 8, 16)));
        sut.Detect().Value!.IsX3D.Should().BeTrue();
    }

    [Fact]
    public void Non_x3d_is_not_flagged()
    {
        var sut = new CpuDetector(WmiReturning(("Intel Core i5-13600K", "GenuineIntel", 14, 20)));
        sut.Detect().Value!.IsX3D.Should().BeFalse();
    }

    [Fact]
    public void Single_ccd_for_8_core_ryzen()
    {
        var sut = new CpuDetector(WmiReturning(("AMD Ryzen 7 7800X3D", "AuthenticAMD", 8, 16)));
        sut.Detect().Value!.CcdCount.Should().Be(1);
    }

    [Fact]
    public void Dual_ccd_for_16_core_ryzen()
    {
        var sut = new CpuDetector(WmiReturning(("AMD Ryzen 9 7950X3D", "AuthenticAMD", 16, 32)));
        sut.Detect().Value!.CcdCount.Should().Be(2);
    }

    [Fact]
    public void Intel_ccd_count_is_zero_not_applicable()
    {
        var sut = new CpuDetector(WmiReturning(("Intel Core i5-13600K", "GenuineIntel", 14, 20)));
        sut.Detect().Value!.CcdCount.Should().Be(0);
    }

    [Fact]
    public void Wmi_failure_propagates_as_result_fail()
    {
        var q = Substitute.For<IWmiQuery>();
        q.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "x"));
        var r = new CpuDetector(q).Detect();
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.WmiFail);
    }

    [Fact]
    public void Empty_rows_fail_cleanly()
    {
        var q = Substitute.For<IWmiQuery>();
        q.Query(Arg.Any<string>()).Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>()));
        new CpuDetector(q).Detect().IsOk.Should().BeFalse();
    }
}
