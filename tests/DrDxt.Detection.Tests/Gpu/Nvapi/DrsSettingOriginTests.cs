using DrDxt.Core;
using DrDxt.Detection.Gpu.Nvapi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu.Nvapi;

/// <summary>
/// The driver already answers "who put this value here?" on every read — NVDRS_SETTING carries
/// settingLocation and isCurrentPredefined — and the product was throwing both away, keeping only
/// the DWORD. That is how a machine with 10218 driver-shipped profiles becomes unreadable: every
/// value looks equally like a user choice.
///
/// Semantics pinned here come from NVIDIA's Driver Settings Programming Guide (PG-12072-001 v01):
/// p.2 "predefined settings" (shipped with the driver) vs "user settings" (edited after install),
/// and p.9-10 "settings that are not from the Current Profile are inherited from the Base or
/// Global profiles. Skip them."
/// </summary>
public class DrsSettingOriginTests
{
    private const uint CurrentProfile = 0, GlobalProfile = 1, BaseProfile = 2, DefaultProfile = 3;

    [Fact]
    public void A_value_on_this_profile_that_is_not_predefined_was_set_by_the_user()
        => DrsSettingOrigin.Describe(CurrentProfile, isCurrentPredefined: 0, isPredefinedValid: 0)
            .Origin.Should().Be(DrsValueOrigin.SetByUser);

    [Fact]
    public void A_predefined_value_on_this_profile_shipped_with_the_driver()
        => DrsSettingOrigin.Describe(CurrentProfile, isCurrentPredefined: 1, isPredefinedValid: 1)
            .Origin.Should().Be(DrsValueOrigin.ShippedWithTheDriver);

    [Fact]
    public void A_value_reported_from_the_global_profile_is_inherited_not_owned()
        => DrsSettingOrigin.Describe(GlobalProfile, isCurrentPredefined: 0, isPredefinedValid: 0)
            .Origin.Should().Be(DrsValueOrigin.InheritedFromGlobalProfile);

    [Fact]
    public void A_value_reported_from_the_base_profile_is_inherited_not_owned()
        => DrsSettingOrigin.Describe(BaseProfile, isCurrentPredefined: 0, isPredefinedValid: 0)
            .Origin.Should().Be(DrsValueOrigin.InheritedFromBaseProfile);

    [Fact]
    public void The_default_profile_location_is_the_driver_default()
        => DrsSettingOrigin.Describe(DefaultProfile, isCurrentPredefined: 1, isPredefinedValid: 1)
            .Origin.Should().Be(DrsValueOrigin.DriverDefault);

    [Fact]
    public void Inheritance_wins_over_the_predefined_flag()
    {
        // TRAP: isCurrentPredefined==0 alone reads as "the user set this". It does not when the
        // value is inherited — the user set it SOMEWHERE ELSE, not on this profile. Reporting
        // "set by user here" would send someone editing a profile that owns nothing.
        var s = DrsSettingOrigin.Describe(GlobalProfile, isCurrentPredefined: 0, isPredefinedValid: 0);

        s.Origin.Should().NotBe(DrsValueOrigin.SetByUser);
        s.Origin.Should().Be(DrsValueOrigin.InheritedFromGlobalProfile);
    }

    [Fact]
    public void An_unknown_location_is_not_guessed()
        => DrsSettingOrigin.Describe(99, isCurrentPredefined: 0, isPredefinedValid: 0)
            .Origin.Should().Be(DrsValueOrigin.Unknown);

    [Fact]
    public void Only_a_user_set_value_is_reported_as_yours()
    {
        DrsSettingOrigin.Describe(CurrentProfile, 0, 0).IsYourOwnChange.Should().BeTrue();

        DrsSettingOrigin.Describe(CurrentProfile, 1, 1).IsYourOwnChange.Should().BeFalse();
        DrsSettingOrigin.Describe(GlobalProfile, 0, 0).IsYourOwnChange.Should().BeFalse();
        DrsSettingOrigin.Describe(BaseProfile, 0, 0).IsYourOwnChange.Should().BeFalse();
        DrsSettingOrigin.Describe(DefaultProfile, 1, 1).IsYourOwnChange.Should().BeFalse();
        DrsSettingOrigin.Describe(99, 0, 0).IsYourOwnChange.Should().BeFalse();
    }

    [Fact]
    public void Every_origin_carries_a_reason_a_player_can_read()
    {
        foreach (var (loc, pre) in new[] { (CurrentProfile, 0u), (CurrentProfile, 1u),
                                           (GlobalProfile, 0u), (BaseProfile, 0u),
                                           (DefaultProfile, 1u), (99u, 0u) })
            DrsSettingOrigin.Describe(loc, pre, pre).Reason.Should().NotBeNullOrWhiteSpace();
    }
}
