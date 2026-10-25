using DrDxt.Core;
using DrDxt.Detection;
using DrDxt.Detection.Gpu;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;
using DrDxt.GpuNative.Nvml;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests;

public class HardwareDetectorTests
{
    private static IWmiQuery FullWmi()
    {
        var q = Substitute.For<IWmiQuery>();
        q.Query(Arg.Is<string>(s => s.Contains("Win32_Processor"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(
            new List<WmiRow> { new(new Dictionary<string, object?> { ["Name"] = "AMD Ryzen 7 7800X3D", ["Manufacturer"] = "AuthenticAMD", ["NumberOfCores"] = 8, ["NumberOfLogicalProcessors"] = 16 }) }));
        q.Query(Arg.Is<string>(s => s.Contains("Win32_PhysicalMemory"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(
            new List<WmiRow> { new(new Dictionary<string, object?> { ["PartNumber"] = "X", ["Manufacturer"] = "Corsair", ["ConfiguredClockSpeed"] = (uint)4800, ["Speed"] = (uint)6000 }) }));
        q.Query(Arg.Is<string>(s => s.Contains("Win32_BaseBoard"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(
            new List<WmiRow> { new(new Dictionary<string, object?> { ["Manufacturer"] = "MSI", ["Product"] = "B650" }) }));
        q.Query(Arg.Is<string>(s => s.Contains("Win32_BIOS"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(
            new List<WmiRow> { new(new Dictionary<string, object?> { ["SMBIOSBIOSVersion"] = "1.A0", ["ReleaseDate"] = "20240115000000.000000+000" }) }));
        q.Query(Arg.Is<string>(s => s.Contains("Win32_SystemEnclosure"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(
            new List<WmiRow> { new(new Dictionary<string, object?> { ["ChassisTypes"] = new[] { (ushort)3 } }) }));
        q.Query(Arg.Is<string>(s => s.Contains("Win32_ComputerSystem"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(
            new List<WmiRow> { new(new Dictionary<string, object?> { ["Model"] = "PC" }) }));
        q.Query(Arg.Is<string>(s => s.Contains("Win32_VideoController"))).Returns(Result<IReadOnlyList<WmiRow>>.Ok(
            new List<WmiRow> { new(new Dictionary<string, object?> { ["DriverVersion"] = "31.0.15.4633" }) }));
        return q;
    }

    private static HardwareDetector BuildSut(IWmiQuery wmi)
    {
        var dxgi = Substitute.For<IDxgiProvider>();
        dxgi.EnumerateAdapters().Returns(Result<IReadOnlyList<DxgiAdapter>>.Ok(
            new List<DxgiAdapter> { new("NVIDIA GeForce RTX 4070", 0x10DE, 12L << 30) }));
        var adlx = Substitute.For<IAdlxProvider>(); adlx.GetSmartAccessMemoryState().Returns(TriState.Off);
        var nv = Substitute.For<INvReBarProvider>(); nv.GetReBarState().Returns(TriState.Unknown);
        var fw = Substitute.For<IFirmwareProvider>(); fw.IsUefi().Returns(true);
        return new HardwareDetector(
            new CpuDetector(wmi), new GpuDetector(dxgi, adlx, wmi, nv, Substitute.For<INvmlNative>()),
            new MemoryDetector(wmi), new BoardBiosDetector(wmi, fw), new ChassisDetector(wmi));
    }

    [Fact]
    public async Task Aggregates_full_inventory()
    {
        var r = await BuildSut(FullWmi()).DetectAsync();
        r.IsOk.Should().BeTrue();
        var hw = r.Value!;
        hw.Cpu.IsX3D.Should().BeTrue();
        hw.Gpus.Should().ContainSingle();
        hw.Memory.Should().ContainSingle();
        hw.Board.Manufacturer.Should().Be("MSI");
        hw.Chassis.Should().Be(ChassisType.Desktop);
    }

    [Fact]
    public async Task Cpu_failure_fails_whole_inventory_no_partial_state()
    {
        var wmi = FullWmi();
        wmi.Query(Arg.Is<string>(s => s.Contains("Win32_Processor")))
           .Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "x"));
        var r = await BuildSut(wmi).DetectAsync();
        // Sans processeur, carte graphique ou carte mère, il n'y a pas d'inventaire : jamais un inventaire à moitié rempli sur
        // ces trois-là (la mémoire et le châssis, eux, se disent non lus).
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.WmiFail);
    }

    // Une lecture WMI manquée sur la mémoire ou le châssis (winmgmt lent au moment du scan) ne fait plus tomber tout le scan :
    // ces deux lectures ont déjà leur « non lu » honnête dans l'inventaire (aucune barrette, châssis inconnu), que le reste du
    // produit sait dire. Le processeur et la carte mère restent indispensables.
    [Fact]
    public async Task Memoire_non_lue_l_inventaire_reste_sans_barrette()
    {
        var wmi = FullWmi();
        wmi.Query(Arg.Is<string>(s => s.Contains("Win32_PhysicalMemory")))
           .Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "délai"));

        var r = await BuildSut(wmi).DetectAsync();

        r.IsOk.Should().BeTrue();
        r.Value!.Memory.Should().BeEmpty();
        r.Value.Cpu.Model.Should().Be("AMD Ryzen 7 7800X3D", "témoin : le reste est lu");
    }

    [Fact]
    public async Task Chassis_non_lu_l_inventaire_dit_chassis_inconnu()
    {
        var wmi = FullWmi();
        wmi.Query(Arg.Is<string>(s => s.Contains("Win32_SystemEnclosure")))
           .Returns(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "délai"));

        var r = await BuildSut(wmi).DetectAsync();

        r.IsOk.Should().BeTrue();
        r.Value!.Chassis.Should().Be(ChassisType.Unknown);
    }
}
