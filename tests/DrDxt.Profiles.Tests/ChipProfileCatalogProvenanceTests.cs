using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Profiles.Tests;

// La porte d'honnêteté. Chaque profil ne doit référencer que de vrais leviers du catalogue, citer
// une source pour chaque gain annoncé, et tout paramètre numérique doit tenir dans l'enveloppe
// de sécurité. C'est la barrière permanente contre un profil inventé ou sans source.
public sealed class ChipProfileCatalogProvenanceTests
{
    private static readonly IChipProfileCatalog Catalog = new ChipProfileCatalog();

    // Les identifiants du catalogue de leviers vivant, résolus exactement comme l'application le compose.
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
                $"profile '{profile.Id}' must only select tweaks that exist in the catalog");
    }

    [Fact]
    public void Every_selection_cites_a_source_and_a_non_empty_claim()
    {
        foreach (var profile in Catalog.All())
        foreach (var selection in profile.Selections)
        {
            selection.Evidence.Source.Should().NotBeNullOrWhiteSpace(
                $"every gain claim in '{profile.Id}' must cite a source");
            selection.Evidence.Claim.Should().NotBeNullOrWhiteSpace();
            selection.Parameters.Should().NotBeNull();
        }
    }

    [Fact]
    public void Generic_profile_surfaces_the_ns1_os_tweaks()
    {
        // Les leviers OS et latence communs aux constructeurs sont recommandés sur chaque machine (leur
        // effet réel est prouvé par l'A/B du moteur, jamais promis). Ils doivent voyager avec le profil générique.
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
        // Honnête : fondé mais pas encore validé sur le matériel.
        Catalog.All().Should().OnlyContain(p => p.Tested == false);
    }

    [Fact]
    public void Nvidia_option_a_parameters_match_the_sourced_values_and_stay_in_bounds()
    {
        var nvidia = Catalog.ByFamily(ChipFamily.NvidiaOptionA);
        nvidia.IsOk.Should().BeTrue();

        var powerLimit = ParamValue(nvidia.Value!, "gpu.power_limit", "PowerLimitPct");

        powerLimit.Should().Be(90);
        // Plus de décalage d'horloge dans aucun profil (règle de la carte).
        nvidia.Value!.Selections.Should().NotContain(s => s.TweakId == "gpu.undervolt");

        // Recoupement avec la vraie enveloppe de sécurité (aucun profil ne peut demander une valeur
        // que la couche d'application refuserait).
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
