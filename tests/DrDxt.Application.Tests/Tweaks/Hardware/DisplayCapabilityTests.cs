using DrDxt.Application.Tweaks.Hardware;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.Hardware;

// -----------------------------------------------------------------------
// « L'écran tourne sous ce qu'il sait faire » : le premier levier dont l'ampleur est
// une arithmétique et non une estimation calibrée.
//
// Le cas qui l'a produit, sur la machine de référence : un écran capable de 200 Hz piloté
// à 144. La règle de latence basse NVIDIA plafonne 3 sous le rafraîchissement, donc la seule
// trace visible était un plafond d'images à 141. Corriger le plafond n'aurait rien corrigé.
// -----------------------------------------------------------------------
public sealed class DisplayCapabilityTests
{
    private static DisplayInfo Display(int current, int max) =>
        new(IsPrimaryGameDisplay: true, SupportsVrr: false, RefreshHz: current, MaxRefreshHz: max);

    [Fact]
    public void A_panel_driven_below_its_maximum_is_a_deficit_with_a_measured_size()
    {
        var d = Display(current: 144, max: 200);

        d.IsBelowCapability.Should().BeTrue();
        d.RefreshHeadroomHz.Should().Be(56);
    }

    [Fact]
    public void A_panel_at_its_maximum_is_not_a_deficit()
    {
        var d = Display(current: 200, max: 200);

        d.IsBelowCapability.Should().BeFalse();
        d.RefreshHeadroomHz.Should().Be(0);
    }

    [Fact]
    public void An_unread_maximum_never_becomes_a_deficit()
    {
        // 0 = « non lu ». Le traiter comme un maximum de zéro, ou comme égal à la cadence
        // courante, inventerait un déficit ou en cacherait un. Il ne doit faire ni l'un ni l'autre.
        var d = Display(current: 144, max: 0);

        d.IsBelowCapability.Should().BeFalse();
        d.RefreshHeadroomHz.Should().Be(0);
    }

    [Fact]
    public void An_unread_current_rate_never_becomes_a_deficit()
    {
        Display(current: 0, max: 200).IsBelowCapability.Should().BeFalse();
    }

    [Fact]
    public void A_maximum_below_the_current_rate_is_not_reported_as_headroom()
    {
        // Ne devrait pas arriver, mais une marge négative annoncée comme gain serait pire que le
        // silence : l'arithmétique ne doit jamais produire un nombre sur lequel le joueur agirait à tort.
        var d = Display(current: 200, max: 144);

        d.IsBelowCapability.Should().BeFalse();
        d.RefreshHeadroomHz.Should().Be(0);
    }

    [Fact]
    public void The_default_construction_stays_source_compatible_and_reads_nothing()
    {
        // Les constructions à trois arguments existantes doivent continuer de compiler et ne pas
        // se mettre à annoncer un maximum qu'elles n'ont jamais lu.
        var legacy = new DisplayInfo(IsPrimaryGameDisplay: true, SupportsVrr: true, RefreshHz: 144);

        legacy.MaxRefreshHz.Should().Be(0);
        legacy.IsBelowCapability.Should().BeFalse();
    }

    [Fact]
    public void The_faster_screen_on_a_two_screen_desk_is_visible()
    {
        // Toute la raison d'être de GetAllDisplays : avec un seul écran en main, rien ne peut
        // dire au joueur que le jeu tourne sur l'écran le plus lent.
        var screens = new[]
        {
            new DisplayInfo(true, false, RefreshHz: 144, MaxRefreshHz: 200, DeviceName: @"\\.\DISPLAY1"),
            new DisplayInfo(false, false, RefreshHz: 165, MaxRefreshHz: 165, DeviceName: @"\\.\DISPLAY2"),
        };

        var fastest = screens.OrderByDescending(s => s.RefreshHz).First();
        fastest.IsPrimaryGameDisplay.Should().BeFalse("the game is on the slower screen here");

        screens.Count(s => s.IsBelowCapability).Should().Be(1);
        screens.Single(s => s.IsBelowCapability).RefreshHeadroomHz.Should().Be(56);
    }
}
