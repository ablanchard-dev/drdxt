using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Display;
using DrDxt.Detection.Gpu.Nvapi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Display;

/// <summary>
/// Pins the defect found on the author's own machine on 02/09/2026: the driver frame cap sat at
/// 141 while the PRIMARY display ran at 200 Hz. 141 is 144 - 3, and 144 is the SECONDARY screen —
/// the cap was computed on the wrong panel and cost frames on every session.
///
/// The whole point of these tests is that 141 is not a magic bad number: on a machine whose
/// primary IS 144 Hz, the very same 141 is correct. Only the RELATION to the primary display
/// decides, which is why <see cref="Same_cap_is_coherent_when_the_primary_is_the_144_screen"/>
/// exists next to <see cref="Cap_matching_a_secondary_display_is_named_as_computed_elsewhere"/>.
/// </summary>
public class FrameCapInferencerTests
{
    private static DisplayInfo Screen(int hz, bool primary) =>
        new(IsPrimaryGameDisplay: primary, SupportsVrr: false, RefreshHz: hz,
            MaxRefreshHz: hz, DeviceName: $@"\.\DISPLAY{(primary ? 1 : 2)}");

    private static readonly DisplayInfo[] TwoScreens = { Screen(200, true), Screen(144, false) };

    [Fact]
    public void Cap_three_under_the_primary_refresh_is_coherent()
        => FrameCapInferencer.Infer(197, TwoScreens)
            .Verdict.Should().Be(FrameCapVerdict.Coherent);

    [Fact]
    public void Cap_matching_a_secondary_display_is_named_as_computed_elsewhere()
    {
        // THE BUG: 141 = 144 - 3, and 144 is the second screen, not the one the game runs on.
        var s = FrameCapInferencer.Infer(141, TwoScreens);

        s.Verdict.Should().Be(FrameCapVerdict.ComputedOnAnotherDisplay);
        s.Reason.Should().Contain("144");
        s.Reason.Should().Contain("200");
    }

    [Fact]
    public void Same_cap_is_coherent_when_the_primary_is_the_144_screen()
    {
        // Same number, opposite verdict. Proves the rule reads the display relation,
        // not a hardcoded 141.
        var s = FrameCapInferencer.Infer(141, new[] { Screen(144, true), Screen(200, false) });

        s.Verdict.Should().Be(FrameCapVerdict.Coherent);
    }

    [Fact]
    public void A_cap_above_the_primary_refresh_never_binds()
        => FrameCapInferencer.Infer(1000, TwoScreens)
            .Verdict.Should().Be(FrameCapVerdict.NotCapping);

    [Fact]
    public void Zero_is_the_driver_off_value_not_a_cap_of_zero_frames()
        => FrameCapInferencer.Infer(0, TwoScreens)
            .Verdict.Should().Be(FrameCapVerdict.NotCapping);

    [Fact]
    public void A_cap_under_the_primary_matching_no_display_is_reported_without_inventing_a_cause()
    {
        var s = FrameCapInferencer.Infer(120, TwoScreens);

        s.Verdict.Should().Be(FrameCapVerdict.CapsBelowPrimary);
        s.Reason.Should().NotContain("144"); // no display explains 120: do not name one
    }

    // ---- qui a pose ce cap ------------------------------------------------------------
    // Le driver repond deja "qui a mis cette valeur la" a chaque lecture, et le produit le
    // gardait pour lui. Or la reponse change ce qu'il faut faire : un cap que l'utilisateur a
    // pose lui-meme se corrige dans le panneau ; un cap livre par NVIDIA pour ce jeu est un
    // reglage d'usine qu'on ne touche pas a l'aveugle.

    [Fact]
    public void A_wrong_cap_the_user_set_says_so()
    {
        var s = FrameCapInferencer.Infer(141, TwoScreens,
            DrsSettingOrigin.Describe(0, isCurrentPredefined: 0, isPredefinedValid: 0));

        s.Verdict.Should().Be(FrameCapVerdict.ComputedOnAnotherDisplay);
        s.Origin!.Value.Origin.Should().Be(DrsValueOrigin.SetByUser);
    }

    [Fact]
    public void A_wrong_cap_the_driver_ships_is_not_blamed_on_the_user()
    {
        var s = FrameCapInferencer.Infer(141, TwoScreens,
            DrsSettingOrigin.Describe(0, isCurrentPredefined: 1, isPredefinedValid: 1));

        s.Origin!.Value.Origin.Should().Be(DrsValueOrigin.ShippedWithTheDriver);
        s.Origin.Value.IsYourOwnChange.Should().BeFalse();
    }

    [Fact]
    public void Without_the_origin_the_verdict_is_unchanged_and_claims_no_author()
    {
        var withOut = FrameCapInferencer.Infer(141, TwoScreens);
        var with = FrameCapInferencer.Infer(141, TwoScreens,
            DrsSettingOrigin.Describe(0, 0, 0));

        withOut.Origin.Should().BeNull("null veut dire non lu, jamais 'personne'");
        withOut.Verdict.Should().Be(with.Verdict, "l'origine informe, elle ne change pas le verdict");
        withOut.Reason.Should().Be(with.Reason, "le motif du cap ne depend pas de qui l'a pose");
    }

    [Fact]
    public void An_unread_cap_is_indeterminate_not_a_clean_bill_of_health()
        => FrameCapInferencer.Infer(null, TwoScreens)
            .Verdict.Should().Be(FrameCapVerdict.Indeterminate);

    [Fact]
    public void No_displays_enumerated_is_indeterminate()
        => FrameCapInferencer.Infer(141, Array.Empty<DisplayInfo>())
            .Verdict.Should().Be(FrameCapVerdict.Indeterminate);

    [Fact]
    public void No_primary_among_the_displays_is_indeterminate()
        => FrameCapInferencer.Infer(141, new[] { Screen(144, false), Screen(200, false) })
            .Verdict.Should().Be(FrameCapVerdict.Indeterminate);

    [Fact]
    public void A_display_reporting_no_refresh_cannot_justify_a_verdict()
        => FrameCapInferencer.Infer(141, new[] { Screen(0, true) })
            .Verdict.Should().Be(FrameCapVerdict.Indeterminate);
}
