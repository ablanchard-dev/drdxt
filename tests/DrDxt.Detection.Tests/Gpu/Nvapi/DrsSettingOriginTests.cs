using DrDxt.Core;
using DrDxt.Detection.Gpu.Nvapi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu.Nvapi;

/// <summary>
/// Le pilote répond déjà « qui a posé cette valeur ? » à chaque lecture (NVDRS_SETTING porte
/// settingLocation et isCurrentPredefined) et le produit jetait les deux, ne gardant que le
/// DWORD. C'est ainsi qu'une machine à 10218 profils livrés par le pilote devient illisible : chaque
/// valeur ressemble autant à un choix du joueur.
///
/// La sémantique figée ici vient du Driver Settings Programming Guide de NVIDIA (PG-12072-001 v01) :
/// p.2 "predefined settings" (livrés avec le pilote) et "user settings" (édités après installation),
/// et p.9-10 "settings that are not from the Current Profile are inherited from the Base or
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
        // Piège : isCurrentPredefined==0 seul se lit « le joueur l'a posé ». Faux quand la valeur
        // est héritée : le joueur l'a posée ailleurs, pas sur ce profil. Annoncer « posé par le joueur
        // ici » enverrait quelqu'un éditer un profil qui ne possède rien.
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
