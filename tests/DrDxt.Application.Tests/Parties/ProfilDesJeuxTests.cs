using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Un profil par jeu, tiré de ses parties ; une partie isolée n'en fait pas un.
public sealed class ProfilDesJeuxTests
{
    private static PartieRelevee P(string jeu, double h, double fps, double low1, BottleneckState g = BottleneckState.CpuBoundOrAppBound)
        => new(jeu, DateTimeOffset.Now.AddHours(h), DateTimeOffset.Now.AddHours(h + 0.3), 6, fps, low1, g);

    [Fact]
    public void Chaque_jeu_assez_joue_a_son_profil_le_plus_joue_d_abord()
    {
        var profils = ProfilDesJeux.Construire([
            P("cod.exe", -5, 130, 90), P("cod.exe", -3, 150, 100), P("cod.exe", -1, 140, 95, BottleneckState.UnknownPartial),
            P("fortnite.exe", -4, 200, 150, BottleneckState.GpuBound), P("fortnite.exe", -2, 210, 160, BottleneckState.GpuBound),
            P("seul.exe", -6, 60, 40)]);

        profils.Select(p => p.Jeu).Should().Equal("cod.exe", "fortnite.exe");
        var cod = profils[0];
        (cod.Parties, cod.FpsMedian, cod.Low1Median).Should().Be((3, 140, 95));
        cod.Goulot.Should().Be(BottleneckState.CpuBoundOrAppBound, "« inconnu » ne l'emporte pas sur un goulot mesuré");
        profils[1].Goulot.Should().Be(BottleneckState.GpuBound);
    }

    // Une dernière partie trop courte ne l'emporte pas sur l'historique mesuré (le conseil Lumen s'en sert).
    [Fact]
    public void Le_goulot_dominant_ignore_une_partie_non_mesuree()
        => ProfilDesJeux.GoulotDominant([P("w", 1, 99, 50), P("w", 2, 99, 50), P("w", 3, 99, 50, BottleneckState.UnknownPartial)])
            .Should().Be(BottleneckState.CpuBoundOrAppBound);
}
