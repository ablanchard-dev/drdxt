using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Hardware;

public class ChassisDetectorTests
{
    private static IWmiQuery Enclosure(int chassisType, string? model = null)
    {
        var q = Substitute.For<IWmiQuery>();
        q.Query(Arg.Is<string>(s => s.Contains("Win32_SystemEnclosure")))
         .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
         { new(new Dictionary<string, object?> { ["ChassisTypes"] = new[] { (ushort)chassisType } }) }));
        q.Query(Arg.Is<string>(s => s.Contains("Win32_ComputerSystem")))
         .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
         { new(new Dictionary<string, object?> { ["Model"] = model ?? "PC" }) }));
        return q;
    }

    [Theory]
    [InlineData(3, ChassisType.Desktop)]   // Desktop
    [InlineData(4, ChassisType.Desktop)]   // Low Profile Desktop
    [InlineData(6, ChassisType.Desktop)]   // Mini Tower
    [InlineData(7, ChassisType.Desktop)]   // Tower
    [InlineData(9, ChassisType.Laptop)]    // Laptop
    [InlineData(10, ChassisType.Laptop)]   // Notebook
    [InlineData(14, ChassisType.Laptop)]   // Sub Notebook
    [InlineData(11, ChassisType.Handheld)] // Hand Held (SMBIOS) → Handheld, jamais Laptop (gating §16)
    [InlineData(30, ChassisType.Handheld)] // Tablet → Handheld
    [InlineData(12, ChassisType.Unknown)]  // Docking Station → ni laptop ni desktop
    public void Maps_smbios_chassis_codes(int code, ChassisType expected)
    {
        new ChassisDetector(Enclosure(code)).Detect().Value!.Should().Be(expected);
    }

    // Revue n° 13 : ces consoles se déclarent « Notebook » en SMBIOS.
    [Theory]
    [InlineData("Claw A1M")]
    [InlineData("GPD WIN 4")]
    [InlineData("ROG Xbox Ally X RC73XA")]
    public void Les_autres_consoles_portables_sont_des_consoles(string modele) =>
        new ChassisDetector(Enclosure(10, modele)).Detect().Value!.Should().Be(ChassisType.Handheld);

    [Fact]
    public void Rog_ally_model_detected_as_handheld()
    {
        // SMBIOS ne code pas "handheld" → on raffine par modèle (Ally/Deck/Legion Go).
        new ChassisDetector(Enclosure(10, "ROG Ally RC71L")).Detect().Value!.Should().Be(ChassisType.Handheld);
    }

    [Fact]
    public void Steam_deck_detected_as_handheld()
    {
        new ChassisDetector(Enclosure(10, "Jupiter")).Detect().Value!.Should().Be(ChassisType.Handheld);
    }

    [Fact]
    public void Unknown_code_is_unknown()
    {
        new ChassisDetector(Enclosure(2)).Detect().Value!.Should().Be(ChassisType.Unknown);
    }
}
