using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Bios.Tests;

/// <summary>
/// AddDrDxtBios() résout toute la surface du docteur BIOS (conseiller + bibliothèque +
/// générateur), et le générateur est câblé sur la vraie bibliothèque embarquée.
/// </summary>
public class BiosServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_advisor_library_and_generator()
    {
        var sp = new ServiceCollection().AddDrDxtBios().BuildServiceProvider();

        sp.GetRequiredService<IBiosAdvisor>().Should().BeOfType<BiosAdvisor>();
        sp.GetRequiredService<IGuideLibrary>().Should().BeOfType<GuideLibrary>();
        sp.GetRequiredService<IGuideGenerator>().Should().BeOfType<GuideGenerator>();
    }

    [Fact]
    public void Registers_the_closed_loop_surface()
    {
        var sp = new ServiceCollection().AddDrDxtBios().BuildServiceProvider();

        sp.GetRequiredService<IBiosClock>().Should().BeOfType<SystemBiosClock>();
        sp.GetRequiredService<IBiosMachineId>().Should().BeOfType<SystemBiosMachineId>();
        sp.GetRequiredService<IPendingBiosChangeStore>().Should().BeOfType<PendingBiosChangeStore>();
        sp.GetRequiredService<IBiosLoopVerifier>().Should().BeOfType<BiosLoopVerifier>();
    }

    [Fact]
    public void Resolved_generator_produces_a_guide_with_recovery()
    {
        var sp = new ServiceCollection().AddDrDxtBios().BuildServiceProvider();
        var gen = sp.GetRequiredService<IGuideGenerator>();

        var hw = new HardwareInventory(
            new CpuInfo("Ryzen", "AuthenticAMD", 8, 16, false, 1),
            new[] { new GpuInfo("GPU", "NVIDIA", 8L << 30, "1.0", TriState.Off) },
            new[] { new MemoryModule("PN", "Mfr", 4800, 6000) },
            new MotherboardInfo("ASUSTeK COMPUTER INC.", "ROG STRIX B650-A"),
            new BiosInfo("1.0", null, true),
            ChassisType.Desktop);

        var finding = new BiosFinding(
            FirmwareSettingKind.XmpExpo, Severity.Major, "t", "r",
            BiosMatrixClass.Guide, TriState.Off, BiosRecommendedAction.Enable, "a", null, false);

        var r = gen.Generate(hw, new[] { finding });
        r.IsOk.Should().BeTrue(r.Error?.Message);
        r.Value!.Recovery.Top.Should().NotBeEmpty();
        r.Value!.Recovery.Bottom.Should().NotBeEmpty();
        r.Value!.Steps.Should().ContainSingle(s => s.Setting == FirmwareSettingKind.XmpExpo);
    }
}
