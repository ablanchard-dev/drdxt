using DrDxt.Application.DependencyInjection;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Profiles.Tests;

// M6 — the adapter projecting the rich recommendation onto Core's IProfileMatcher.
public sealed class CoreProfileMatcherAdapterTests
{
    private static IProfileMatcher NewAdapter() =>
        new CoreProfileMatcherAdapter(new ChipProfileMatcher(new ChipProfileCatalog()));

    private static IReadOnlyCollection<string> RealCatalogTweakIds()
    {
        using var provider = new ServiceCollection().AddDrDxtApplication().BuildServiceProvider();
        return provider.GetRequiredService<ITweakCatalog>().All().Select(t => t.Id).ToHashSet();
    }

    [Fact]
    public void Match_projects_recommended_tweak_ids_fix_misconfig_first_and_deduped()
    {
        var hw = TestInventory.Build(TestInventory.AmdX3dDualCcd(), new[] { TestInventory.NvidiaRtxDesktop() });

        var result = NewAdapter().Match(hw);

        result.IsOk.Should().BeTrue();
        var ids = result.Value!.RecommendedTweakIds;

        ids.Should().OnlyHaveUniqueItems();
        // fix-misconfig first: every fixit.* precedes every latency.* and gpu.*
        var lastFixit = ids.Select((id, i) => (id, i)).Where(x => x.id.StartsWith("fixit.")).Select(x => x.i).DefaultIfEmpty(-1).Max();
        var firstNonFixit = ids.Select((id, i) => (id, i)).Where(x => !x.id.StartsWith("fixit.")).Select(x => x.i).DefaultIfEmpty(int.MaxValue).Min();
        lastFixit.Should().BeLessThan(firstNonFixit);
        // includes the headline fix + a GPU tune for this combo.
        ids.Should().Contain("fixit.x3d_ccd").And.Contain("gpu.power_limit");
    }

    [Fact]
    public void Match_recommended_ids_are_all_real_catalog_tweaks()
    {
        var real = RealCatalogTweakIds();
        var hw = TestInventory.Build(TestInventory.AmdX3dDualCcd(), new[] { TestInventory.NvidiaRtxDesktop() });

        var result = NewAdapter().Match(hw);

        result.Value!.RecommendedTweakIds.Should().OnlyContain(id => real.Contains(id));
    }

    [Fact]
    public void Match_composite_is_not_tested_until_all_parts_are()
    {
        var hw = TestInventory.Build(TestInventory.Intel(), new[] { TestInventory.NvidiaRtxDesktop() });

        var result = NewAdapter().Match(hw);

        result.Value!.Tested.Should().BeFalse();
    }

    [Fact]
    public void Match_fills_hardware_patterns_from_the_classification()
    {
        var x3dNv = NewAdapter().Match(
            TestInventory.Build(TestInventory.AmdX3dDualCcd(), new[] { TestInventory.NvidiaRtxDesktop() }));
        x3dNv.Value!.Match.CpuPattern.Should().NotBeNull();
        x3dNv.Value.Match.GpuPattern.Should().NotBeNull();

        var generic = NewAdapter().Match(TestInventory.Build(TestInventory.Intel()));
        generic.Value!.Match.CpuPattern.Should().BeNull();
        generic.Value.Match.GpuPattern.Should().BeNull();
    }

    [Fact]
    public void Match_null_inventory_is_fail_closed()
    {
        var result = NewAdapter().Match(null!);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(ErrorCodes.ProfileInputInvalid);
    }
}
