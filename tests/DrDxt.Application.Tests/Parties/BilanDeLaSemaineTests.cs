using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// 04/10/2026 (plan 3b.5) : la semaine comptée ; les FPS comparés au même jeu, seulement si les deux semaines en ont.
public sealed class BilanDeLaSemaineTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);
    private static PartieRelevee P(string jeu, double jours, double fps)
        => new(jeu, Maintenant.AddDays(-jours).AddMinutes(-20), Maintenant.AddDays(-jours), 6, fps, fps * 0.7, BottleneckState.CpuBoundOrAppBound);

    [Fact]
    public void La_semaine_se_compte_et_se_compare_a_la_precedente_sur_le_meme_jeu()
    {
        var b = BilanDeLaSemaine.Construire(
            [P("cod.exe", 1, 150), P("cod.exe", 2, 140), P("autre.exe", 3, 300), P("cod.exe", 10, 120)],
            [new Changement(Maintenant.AddDays(-2), "BIOS"), new Changement(Maintenant.AddDays(-2), "p", null, "pose:x"), new Changement(Maintenant.AddDays(-9), "vieux")],
            [Maintenant.AddDays(-1), Maintenant.AddDays(-30)], [], Maintenant);

        b.Parties.Should().Be(3);
        b.FpsMedian.Should().Be(145, "médiane du jeu le plus joué, sans l'autre jeu");
        b.FpsMedianSemainePrecedente.Should().Be(120);
        (b.Changements, b.Arrets, b.Erreurs).Should().Be((1, 1, 0));
    }

    [Fact]
    public void Sans_partie_la_semaine_d_avant_la_comparaison_est_tue()
        => BilanDeLaSemaine.Construire([P("cod.exe", 1, 150)], [], [], [], Maintenant).FpsMedianSemainePrecedente.Should().BeNull();
}
