using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Profiles.Tests;

// La classification des puces est le cœur prudent et fermé de l'apparieur.
public sealed class ChipClassifierTests
{
    [Theory]
    [InlineData("AuthenticAMD", true)]
    [InlineData("Advanced Micro Devices, Inc.", true)]
    [InlineData("ATI Technologies Inc.", true)]
    [InlineData("NVIDIA Corporation", false)]
    [InlineData("Intel Corporation", false)]
    [InlineData("GenuineIntel", false)]
    [InlineData("NVIDIA", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsAmd_classifies_vendor(string? vendor, bool expected) =>
        ChipClassifier.IsAmd(vendor).Should().Be(expected);

    [Theory]
    [InlineData("NVIDIA", true)]
    [InlineData("nvidia corporation", true)]
    [InlineData("AuthenticAMD", false)]
    [InlineData(null, false)]
    public void IsNvidia_classifies_vendor(string? vendor, bool expected) =>
        ChipClassifier.IsNvidia(vendor).Should().Be(expected);

    [Fact]
    public void IsAmdX3dDualCcd_true_only_for_dual_ccd_amd_x3d()
    {
        ChipClassifier.IsAmdX3dDualCcd(TestInventory.AmdX3dDualCcd()).Should().BeTrue();
    }

    [Fact]
    public void IsAmdX3dDualCcd_false_for_single_ccd_x3d() // 7800X3D/9800X3D : hors périmètre
    {
        ChipClassifier.IsAmdX3dDualCcd(TestInventory.AmdX3dSingleCcd()).Should().BeFalse();
    }

    [Fact]
    public void IsAmdX3dDualCcd_false_for_non_x3d_and_intel_and_null()
    {
        ChipClassifier.IsAmdX3dDualCcd(TestInventory.AmdNonX3d()).Should().BeFalse();
        ChipClassifier.IsAmdX3dDualCcd(TestInventory.Intel()).Should().BeFalse();
        ChipClassifier.IsAmdX3dDualCcd(null).Should().BeFalse();
    }

    [Fact]
    public void IsTunableNvidiaGpu_true_for_discrete_desktop_geforce()
    {
        ChipClassifier.IsTunableNvidiaGpu(TestInventory.NvidiaRtxDesktop()).Should().BeTrue();
        ChipClassifier.IsTunableNvidiaGpu(TestInventory.NvidiaRtxDesktop("NVIDIA GeForce GTX 1660 SUPER")).Should().BeTrue();
    }

    [Fact]
    public void IsTunableNvidiaGpu_false_for_mobile_quadro_amd_igpu_null()
    {
        ChipClassifier.IsTunableNvidiaGpu(TestInventory.NvidiaRtxMobile()).Should().BeFalse(); // verrouillé par le pilote
        ChipClassifier.IsTunableNvidiaGpu(TestInventory.NvidiaQuadro()).Should().BeFalse();    // pas un nom GeForce
        ChipClassifier.IsTunableNvidiaGpu(TestInventory.AmdRadeonDesktop()).Should().BeFalse(); // chemin ADLX séparé
        ChipClassifier.IsTunableNvidiaGpu(TestInventory.IntelIgpu()).Should().BeFalse();        // iGPU
        ChipClassifier.IsTunableNvidiaGpu(null).Should().BeFalse();
    }

    [Fact]
    public void HasTunableNvidiaDesktopGpu_requires_desktop_chassis()
    {
        var nv = new[] { TestInventory.NvidiaRtxDesktop() };
        ChipClassifier.HasTunableNvidiaDesktopGpu(
            TestInventory.Build(TestInventory.Intel(), nv, ChassisType.Desktop)).Should().BeTrue();
        ChipClassifier.HasTunableNvidiaDesktopGpu(
            TestInventory.Build(TestInventory.Intel(), nv, ChassisType.Laptop)).Should().BeFalse();
    }

    [Fact]
    public void HasTunableNvidiaDesktopGpu_false_when_only_igpu_or_null()
    {
        ChipClassifier.HasTunableNvidiaDesktopGpu(
            TestInventory.Build(TestInventory.Intel(), new[] { TestInventory.IntelIgpu() })).Should().BeFalse();
        ChipClassifier.HasTunableNvidiaDesktopGpu(null).Should().BeFalse();
    }

    // L'option A ne vise que les GeForce : une carte professionnelle au nom proche (Quadro RTX, RTX … Ada Generation) n'est
    // pas réglée comme une carte de jeu.
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060", true)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", true)]
    [InlineData("NVIDIA Quadro RTX 4000", false)]
    [InlineData("NVIDIA RTX 2000 Ada Generation", false)]
    [InlineData("NVIDIA RTX A4000", false)]
    public void Seule_une_GeForce_est_reglable_par_l_option_A(string modele, bool reglable)
        => ChipClassifier.IsTunableNvidiaGpu(new GpuInfo(modele, "NVIDIA", 8L << 30, "1", TriState.On)).Should().Be(reglable);
}
