using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// 29/09/2026 (boucle « vivant et prouvé », A3) : aucune partie jouée ne laissait de trace. Pendant une partie, des
// captures courtes ; à la fin, une ligne : FPS et 1 % low médians, le goulot le plus vu, le nombre de captures.
public sealed class ReleveDesPartiesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 21, 0, 0, TimeSpan.Zero);

    private static CaptureEnPartie C(int minute, double fps, double low, BottleneckState goulot)
        => new(T0.AddMinutes(minute), fps, low, goulot);

    // B4 (10/10/2026) : la latence d'affichage à sa médiane ; aucune capture ne l'a = non livrée, jamais zéro.
    [Fact]
    public void La_latence_d_affichage_se_resume_a_sa_mediane_ou_reste_non_livree()
    {
        var avec = ReleveDesParties.Resumer("cod.exe",
        [
            C(1, 130, 95, BottleneckState.CpuBoundOrAppBound) with { LatenceAffichageMs = 24 },
            C(4, 140, 100, BottleneckState.CpuBoundOrAppBound) with { LatenceAffichageMs = 20 },
            C(7, 120, 80, BottleneckState.MixedGpuLeaning) with { LatenceAffichageMs = 30 },
        ])!;
        avec.LatenceAffichageMedianMs.Should().Be(24);

        ReleveDesParties.Resumer("cod.exe", [C(1, 130, 95, BottleneckState.CpuBoundOrAppBound), C(4, 140, 100, BottleneckState.CpuBoundOrAppBound)])!
            .LatenceAffichageMedianMs.Should().BeNull();
    }

    [Fact]
    public void Une_partie_se_resume_en_medianes_et_en_goulot_le_plus_vu()
    {
        var p = ReleveDesParties.Resumer("cod.exe",
        [
            C(1, 130, 95, BottleneckState.CpuBoundOrAppBound),
            C(4, 140, 100, BottleneckState.CpuBoundOrAppBound),
            C(7, 120, 80, BottleneckState.MixedGpuLeaning),
        ])!;

        p.Jeu.Should().Be("cod.exe");
        p.Captures.Should().Be(3);
        p.FpsMedian.Should().Be(130);
        p.Low1Median.Should().Be(95);
        p.Goulot.Should().Be(BottleneckState.CpuBoundOrAppBound);
        p.Debut.Should().Be(T0.AddMinutes(1));
        p.Fin.Should().Be(T0.AddMinutes(7));
    }

    // 04/10/2026 (plan 1.1) : en partie sous 90 % de ce que le banc de 45 s tient, le processeur ralentit en jeu.
    [Theory]
    [InlineData(4300.0, 5190.0, true)]
    [InlineData(5100.0, 5190.0, false)]
    [InlineData(null, 5190.0, false)]
    [InlineData(4300.0, null, false)]
    public void Le_processeur_ralentit_en_partie_sous_neuf_dixiemes_du_banc(double? enPartie, double? banc, bool attendu)
    {
        var p = new PartieRelevee("cod.exe", DateTimeOffset.Now, DateTimeOffset.Now, 5, 120, 90,
            BottleneckState.CpuBoundOrAppBound, enPartie, 120, BancMhz: banc);
        ReleveDesParties.RalentitEnPartie(p).Should().Be(attendu);
    }

    [Fact]
    public void Les_captures_sans_releve_processeur_ne_faussent_pas_la_mediane()
    {
        var t = DateTimeOffset.Now;
        var p = ReleveDesParties.Resumer("cod.exe", [
            new CaptureEnPartie(t, 120, 90, BottleneckState.CpuBoundOrAppBound, 5000, 130),
            new CaptureEnPartie(t, 120, 90, BottleneckState.CpuBoundOrAppBound),
            new CaptureEnPartie(t, 120, 90, BottleneckState.CpuBoundOrAppBound, 4000, 110)])!;
        p.MhzMedian.Should().Be(4500, "la capture sans relevé n'est pas un zéro");
        // Deux captures : une seule ne fait plus une partie (revue F-5, 06/10).
        ReleveDesParties.Resumer("cod.exe", [new CaptureEnPartie(t, 120, 90, BottleneckState.GpuBound),
                new CaptureEnPartie(t, 120, 90, BottleneckState.GpuBound)])!
            .MhzMedian.Should().BeNull();
    }

    [Fact]
    public void Sans_capture_il_n_y_a_pas_de_partie_a_retenir()
        => ReleveDesParties.Resumer("cod.exe", []).Should().BeNull();

    [Fact]
    public void Un_goulot_inconnu_ne_l_emporte_pas_sur_un_goulot_mesure()
        => ReleveDesParties.Resumer("cod.exe",
            [C(1, 130, 95, BottleneckState.UnknownPartial), C(2, 130, 95, BottleneckState.UnknownPartial), C(3, 130, 95, BottleneckState.GpuBound)])!
            .Goulot.Should().Be(BottleneckState.GpuBound, "« inconnu » n'est pas un verdict qui gagne au nombre");

    [Fact]
    public void Seules_les_vingt_dernieres_parties_sont_gardees()
    {
        var liste = new List<PartieRelevee>();
        for (var i = 0; i < 25; i++)
            liste = ReleveDesParties.Ajouter(liste, new PartieRelevee("cod.exe", T0.AddHours(i), T0.AddHours(i).AddMinutes(20), 3, 100 + i, 80, BottleneckState.GpuBound)).ToList();

        liste.Should().HaveCount(ReleveDesParties.PartiesGardees);
        liste[0].FpsMedian.Should().Be(124, "la plus récente d'abord");
        liste[^1].FpsMedian.Should().Be(105);
    }
}
