using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Profiles.Tests;

// M6 — DI wiring resolves the profile layer (mirrors AddDrDxtDetection/Application tests).
public sealed class ProfilesServiceCollectionExtensionsTests
{
    private static ServiceProvider Build() =>
        new ServiceCollection().AddDrDxtProfiles().BuildServiceProvider();

    [Fact]
    public void Resolves_the_rich_catalog_and_matcher()
    {
        using var provider = Build();

        provider.GetRequiredService<IChipProfileCatalog>().Should().BeOfType<ChipProfileCatalog>();
        provider.GetRequiredService<IChipProfileMatcher>().Should().BeOfType<ChipProfileMatcher>();
    }

    [Fact]
    public void Resolves_the_core_IProfileMatcher_via_the_adapter_and_it_works_end_to_end()
    {
        using var provider = Build();

        var matcher = provider.GetRequiredService<IProfileMatcher>();
        matcher.Should().BeOfType<CoreProfileMatcherAdapter>();

        var result = matcher.Match(TestInventory.Build(TestInventory.AmdX3dDualCcd()));

        result.IsOk.Should().BeTrue();
        result.Value!.RecommendedTweakIds.Should().Contain("fixit.x3d_ccd");
    }
}
