using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// R2 (SAFE half) — GpuArchitectureGate: Blackwell / RTX 50 detection + the
// voltage-undervolt gate.
//
// NVIDIA removed direct voltage writes on Blackwell (recipe). This pure classifier
// is the ONE source of truth the GPU read seam (INvApiInfo) and a later undervolt
// gate share. Blackwell ⇒ voltage-undervolt not supported; Ada-or-older ⇒
// supported; Unknown ⇒ conservatively not supported (never assume a write is safe).
// -----------------------------------------------------------------------
public sealed class GpuArchitectureGateTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", GpuArchitecture.Blackwell)]
    [InlineData("NVIDIA GeForce RTX 5080", GpuArchitecture.Blackwell)]
    [InlineData("RTX 5070 Ti", GpuArchitecture.Blackwell)]
    [InlineData("NVIDIA GeForce RTX 4090", GpuArchitecture.AdaOrOlder)]
    [InlineData("NVIDIA GeForce RTX 4060", GpuArchitecture.AdaOrOlder)]
    [InlineData("NVIDIA GeForce RTX 3080", GpuArchitecture.AdaOrOlder)]
    [InlineData("NVIDIA GeForce RTX 2070 SUPER", GpuArchitecture.AdaOrOlder)]
    [InlineData("NVIDIA GeForce GTX 1660 Ti", GpuArchitecture.AdaOrOlder)]
    // Le classifieur ne parlait que de NVIDIA : toute carte AMD ressortait Unknown, et
    // `SupportsVoltageUndervolt` compensait en rendant `true` pour tout non-NVIDIA. Depuis D5 il
    // nomme les deux architectures AMD qui comptent, ce qui est precisement ce qui permet de
    // refuser RDNA3 sans refuser RDNA4.
    [InlineData("AMD Radeon RX 7900 XTX", GpuArchitecture.Rdna3)]
    [InlineData("AMD Radeon RX 9070 XT", GpuArchitecture.Rdna4)]
    [InlineData("Intel Arc A770", GpuArchitecture.Unknown)]
    [InlineData("", GpuArchitecture.Unknown)]
    public void ClassifyByName_maps_marketing_names_to_architecture(string model, GpuArchitecture expected) =>
        GpuArchitectureGate.ClassifyByName(model).Should().Be(expected);

    [Theory]
    [InlineData(GpuArchitecture.Blackwell, false)]   // voltage path LOCKED OUT
    [InlineData(GpuArchitecture.AdaOrOlder, true)]    // legacy writes honoured
    [InlineData(GpuArchitecture.Unknown, false)]      // conservative: do not assume safe
    public void SupportsVoltageUndervolt_gates_blackwell_off(GpuArchitecture arch, bool expected) =>
        GpuArchitectureGate.SupportsVoltageUndervolt(arch).Should().Be(expected);

    // --- The GpuInfo-level gate (the public surface a later undervolt tweak uses). ---

    private static GpuInfo Nv(string model) => new(model, "NVIDIA", 12L << 30, "551.23", TriState.On);

    [Fact]
    public void Gate_blocks_voltage_undervolt_on_blackwell()
    {
        GpuArchitectureGate.SupportsVoltageUndervolt(Nv("NVIDIA GeForce RTX 5080")).Should().BeFalse();
    }

    [Fact]
    public void Gate_allows_voltage_undervolt_on_ada()
    {
        GpuArchitectureGate.SupportsVoltageUndervolt(Nv("NVIDIA GeForce RTX 4060")).Should().BeTrue();
    }

    [Fact]
    public void Gate_voltage_undervolt_refuses_RDNA3_because_it_was_never_write_tested()
    {
        // Ce test disait l'inverse, et il encodait le defaut. Il affirmait que le voltage
        // s'applique a une RX 7900 XTX « parce que le verrou Blackwell ne concerne pas AMD ».
        // C'etait vrai, et insuffisant : RDNA3 est la seule architecture AMD qui garde le
        // voltage manuel, et la seule que personne n'a jamais testee en ecriture.
        //
        // La decision D5 (fail-closed materiel) la desactive jusqu'a un test reel. Corrige le
        // 02/09/2026 avec le code, pas contre lui.
        var rdna3 = new GpuInfo("AMD Radeon RX 7900 XTX", "AMD", 24L << 30, "24.1.1", TriState.On);
        GpuArchitectureGate.SupportsVoltageUndervolt(rdna3).Should().BeFalse();
    }

    [Fact]
    public void Gate_voltage_undervolt_still_applies_to_a_validated_amd_card()
    {
        // Le fail-closed n'est pas un refus general : RDNA4 a ete validee EN VIE sur une
        // RX 9070 XT (ecriture, revert, empreinte propre).
        var rdna4 = new GpuInfo("AMD Radeon RX 9070 XT", "AMD", 16L << 30, "25.1.1", TriState.On);
        GpuArchitectureGate.SupportsVoltageUndervolt(rdna4).Should().BeTrue();
    }
}
