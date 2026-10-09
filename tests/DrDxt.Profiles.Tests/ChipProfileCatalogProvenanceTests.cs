using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Profiles.Tests;

// M6 — the HONESTY gate. Every profile must reference only real M2 catalog tweaks, must
// cite a source for every claimed gain, and any numeric parameter must sit inside the M2
// safety envelope. This is the permanent barrier against an invented/unsourced profile.
public sealed class ChipProfileCatalogProvenanceTests
{
    private static readonly IChipProfileCatalog Catalog = new ChipProfileCatalog();

    // The ids of the live M2 tweak catalog, resolved exactly as the app composes it.
    private static IReadOnlyCollection<string> RealCatalogTweakIds()
    {
        using var provider = new ServiceCollection().AddDrDxtApplication().BuildServiceProvider();
        return provider.GetRequiredService<ITweakCatalog>().All().Select(t => t.Id).ToHashSet();
    }

    [Fact]
    public void Catalog_has_the_three_foundation_profiles_with_distinct_families()
    {
        var families = Catalog.All().Select(p => p.Family).ToList();
        families.Should().BeEquivalentTo(new[]
        {
            ChipFamily.GenericSafe, ChipFamily.AmdX3dDualCcd, ChipFamily.NvidiaOptionA,
        });
        families.Should().OnlyHaveUniqueItems();
        Catalog.Generic.Family.Should().Be(ChipFamily.GenericSafe);
    }

    [Fact]
    public void Every_profile_selection_references_a_real_m2_catalog_tweak()
    {
        var real = RealCatalogTweakIds();

        foreach (var profile in Catalog.All())
        foreach (var selection in profile.Selections)
            real.Should().Contain(selection.TweakId,
                $"profile '{profile.Id}' must only select tweaks that exist in the M2 catalog");
    }

    [Fact]
    public void Every_selection_cites_a_source_and_a_non_empty_claim()
    {
        foreach (var profile in Catalog.All())
        foreach (var selection in profile.Selections)
        {
            selection.Evidence.Source.Should().NotBeNullOrWhiteSpace(
                $"every gain claim in '{profile.Id}' must cite a source (honesty §11)");
            selection.Evidence.Claim.Should().NotBeNullOrWhiteSpace();
            selection.Parameters.Should().NotBeNull();
        }
    }

    [Fact]
    public void Generic_profile_surfaces_the_ns1_os_tweaks()
    {
        // NS-1: the cross-vendor OS/latency tweaks are recommended on every machine (their real
        // effect is proven by the engine's A/B, never promised). They must ride the generic profile.
        Catalog.Generic.Selections.Select(s => s.TweakId).Should().Contain(new[]
        {
            "latency.hagsOn", "latency.gameModeOn",
            "latency.gameDvrBackgroundOff", "latency.vrrFlipModelOn", "latency.mouse_acceleration_off",
        });
    }

    [Fact]
    public void Foundation_profiles_are_all_safe_level()
    {
        Catalog.All()
            .SelectMany(p => p.Selections)
            .Should().OnlyContain(s => s.Evidence.Safety == SafetyLevel.Safe);
    }

    [Fact]
    public void Foundation_profiles_are_untested_until_team_waves_validate_them()
    {
        // Honest: grounded but not yet hardware-validated (cahier §10).
        Catalog.All().Should().OnlyContain(p => p.Tested == false);
    }

    [Fact]
    public void Nvidia_option_a_parameters_match_the_sourced_values_and_stay_in_bounds()
    {
        var nvidia = Catalog.ByFamily(ChipFamily.NvidiaOptionA);
        nvidia.IsOk.Should().BeTrue();

        var powerLimit = ParamValue(nvidia.Value!, "gpu.power_limit", "PowerLimitPct");

        powerLimit.Should().Be(90);  // DECISION-undervolt Option A
        // 08/10/2026 (V2) : plus de décalage d'horloge dans aucun profil (règle du 29/09).
        nvidia.Value!.Selections.Should().NotContain(s => s.TweakId == "gpu.undervolt");

        // Cross-check against the real M2 safety envelope (no profile can request a value
        // the apply layer would refuse).
        GpuSafeBounds.IsPowerLimitSafe(powerLimit).Should().BeTrue();
    }

    [Fact]
    public void ByFamily_unknown_family_fails_clean()
    {
        var got = Catalog.ByFamily((ChipFamily)999);

        got.IsOk.Should().BeFalse();
        got.Error!.Value.Code.Should().Be(ErrorCodes.TweakNotApplicable);
    }

    private static int ParamValue(ChipProfile profile, string tweakId, string key) =>
        profile.Selections
            .Single(s => s.TweakId == tweakId)
            .Parameters.Single(p => p.Key == key)
            .Value;
}
