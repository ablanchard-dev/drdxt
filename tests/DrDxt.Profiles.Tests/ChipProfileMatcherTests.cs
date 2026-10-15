using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Profiles.Tests;

// L'apparieur : puce détectée → profil(s) recommandé(s), prudent et fermé.
public sealed class ChipProfileMatcherTests
{
    private static ChipProfileMatcher NewMatcher() => new(new ChipProfileCatalog());

    [Fact]
    public void Generic_baseline_is_always_recommended_first()
    {
        var rec = NewMatcher().Recommend(TestInventory.Build(TestInventory.Intel()));

        rec.IsOk.Should().BeTrue();
        rec.Value!.Profiles.Should().NotBeEmpty();
        rec.Value.Profiles[0].Family.Should().Be(ChipFamily.GenericSafe);
    }

    [Fact]
    public void Unknown_chip_gets_generic_only_never_a_guess()
    {
        // Intel non-X3D CPU + AMD discrete GPU: neither curated family applies.
        var hw = TestInventory.Build(TestInventory.Intel(), new[] { TestInventory.AmdRadeonDesktop() });

        var rec = NewMatcher().Recommend(hw);

        rec.IsOk.Should().BeTrue();
        rec.Value!.Profiles.Select(p => p.Family).Should().Equal(ChipFamily.GenericSafe);
        rec.Value.Classification.IsAmdX3dDualCcd.Should().BeFalse();
        rec.Value.Classification.HasTunableNvidiaGpu.Should().BeFalse();
    }

    [Fact]
    public void Dual_ccd_x3d_machine_gets_generic_plus_x3d()
    {
        var hw = TestInventory.Build(TestInventory.AmdX3dDualCcd(), new[] { TestInventory.AmdRadeonDesktop() });

        var rec = NewMatcher().Recommend(hw);

        rec.Value!.Profiles.Select(p => p.Family)
            .Should().BeEquivalentTo(new[] { ChipFamily.GenericSafe, ChipFamily.AmdX3dDualCcd });
        rec.Value.Classification.IsAmdX3dDualCcd.Should().BeTrue();
    }

    [Fact]
    public void Nvidia_desktop_machine_gets_generic_plus_option_a()
    {
        var hw = TestInventory.Build(TestInventory.Intel(), new[] { TestInventory.NvidiaRtxDesktop() });

        var rec = NewMatcher().Recommend(hw);

        rec.Value!.Profiles.Select(p => p.Family)
            .Should().BeEquivalentTo(new[] { ChipFamily.GenericSafe, ChipFamily.NvidiaOptionA });
        rec.Value.Classification.HasTunableNvidiaGpu.Should().BeTrue();
    }

    [Fact]
    public void X3d_plus_nvidia_machine_gets_all_three_profiles()
    {
        var hw = TestInventory.Build(TestInventory.AmdX3dDualCcd(), new[] { TestInventory.NvidiaRtxDesktop() });

        var rec = NewMatcher().Recommend(hw);

        rec.Value!.Profiles.Select(p => p.Family)
            .Should().BeEquivalentTo(new[]
            {
                ChipFamily.GenericSafe, ChipFamily.AmdX3dDualCcd, ChipFamily.NvidiaOptionA,
            });
    }

    [Fact]
    public void Single_ccd_x3d_does_not_get_the_x3d_profile()
    {
        var hw = TestInventory.Build(TestInventory.AmdX3dSingleCcd(), new[] { TestInventory.AmdRadeonDesktop() });

        var rec = NewMatcher().Recommend(hw);

        rec.Value!.Profiles.Select(p => p.Family).Should().NotContain(ChipFamily.AmdX3dDualCcd);
    }

    [Fact]
    public void Null_inventory_is_fail_closed()
    {
        var rec = NewMatcher().Recommend(null!);

        rec.IsOk.Should().BeFalse();
        rec.Error!.Value.Code.Should().Be(ErrorCodes.ProfileInputInvalid);
    }

    [Fact]
    public void Classification_reasons_are_always_populated()
    {
        var rec = NewMatcher().Recommend(TestInventory.Build(TestInventory.Intel()));

        rec.Value!.Classification.Reasons.Should().NotBeEmpty();
        rec.Value.Classification.Reasons.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r));
    }
}
