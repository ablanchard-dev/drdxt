using FluentAssertions;

namespace DrDxt.Profiles.Tests;

// Invariants du modèle (records et l'aide d'ordre « mauvaise configuration d'abord »).
public sealed class ProfileModelTests
{
    private static ProfileTweakSelection Sel(string id, ProfileIntent intent) =>
        new(id, intent, Array.Empty<ProfileParameter>(),
            new GainEvidence("claim", "source", GainConfidence.Sourced, SafetyLevel.Safe));

    [Fact]
    public void OrderedTweakIds_is_fix_misconfig_then_latency_then_gpu_tune()
    {
        var profile = new ChipProfile(
            "p", "p.name", ChipFamily.NvidiaOptionA,
            new[]
            {
                Sel("gpu.power_limit", ProfileIntent.GpuTune),
                Sel("fixit.power_plan", ProfileIntent.FixMisconfig),
                Sel("latency.reflex", ProfileIntent.Latency),
            },
            Tested: false, "summary");

        profile.OrderedTweakIds.Should().Equal("fixit.power_plan", "latency.reflex", "gpu.power_limit");
    }

    [Fact]
    public void ProfileParameter_equality_is_by_value()
    {
        new ProfileParameter("CoreOffsetMhz", 75, "MHz")
            .Should().Be(new ProfileParameter("CoreOffsetMhz", 75, "MHz"));
    }

    [Fact]
    public void GainEvidence_carries_confidence_and_safety()
    {
        var e = new GainEvidence("c", "s", GainConfidence.Measured, SafetyLevel.Safe);
        e.Confidence.Should().Be(GainConfidence.Measured);
        e.Safety.Should().Be(SafetyLevel.Safe);
    }

    [Fact]
    public void ChipProfile_equality_is_by_value_with_shared_lists()
    {
        IReadOnlyList<ProfileTweakSelection> selections = new[] { Sel("fixit.power_plan", ProfileIntent.FixMisconfig) };
        var a = new ChipProfile("p", "p.name", ChipFamily.GenericSafe, selections, false, "s");
        var b = new ChipProfile("p", "p.name", ChipFamily.GenericSafe, selections, false, "s");
        a.Should().Be(b);
    }
}
