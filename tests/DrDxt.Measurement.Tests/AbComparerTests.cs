using DrDxt.Core;
using DrDxt.Measurement;
using FluentAssertions;
using Xunit;

namespace DrDxt.Measurement.Tests;

public class AbComparerTests
{
    // moyenne et 1 % bas bougent ensemble (moy.*0,7) : les cas simples « tout monte / tout baisse ».
    private static BenchmarkResult Bench(double avg, double moe) =>
        new(avg, avg * 0.7, avg * 0.5, new List<double>(), null, moe, 5, P1LowMarginOfError: moe);

    // Moyenne, 1 % bas et marge explicites pour faire varier la régularité des images indépendamment
    // de la moyenne : tout l'enjeu du critère des leviers d'efficacité (undervolt).
    private static BenchmarkResult Bench(double avg, double p1Low, double moe) =>
        new(avg, p1Low, avg * 0.5, new List<double>(), null, moe, 5, P1LowMarginOfError: moe);

    // Les lows se jugeaient avec la marge des FPS moyens. Ici ils bougent de 6 FPS, au-dela
    // de la marge des moyennes (2 + 2) mais dans la leur (5 + 5) : ce n'est ni un gain ni une regression.
    [Fact]
    public void Les_lows_se_jugent_avec_leur_propre_marge_pas_celle_des_moyennes()
    {
        static BenchmarkResult B(double p1) => new(100, p1, 50, new List<double>(), null, 2, 5, P1LowMarginOfError: 5);

        AbComparer.Compare(B(70), B(76)).IsMeasurableGain.Should().BeFalse();
        AbComparer.Compare(B(76), B(70)).SummaryKey.Should().Be("Scan.Ab.NoMeasurableGain");
        AbComparer.Compare(B(70), B(81)).IsMeasurableGain.Should().BeTrue("temoin : au-dela de leur marge, c'est un gain");
    }

    [Fact]
    public void Gain_when_delta_exceeds_combined_margins()
    {
        var before = Bench(100, 1);
        var after = Bench(110, 1); // delta 10 > marges combinées (1+1=2)
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeTrue();
        v.SummaryKey.Should().Be("Scan.Ab.MeasurableGain");
    }

    [Fact]
    public void No_gain_when_delta_inside_combined_margins()
    {
        var before = Bench(100, 3);
        var after = Bench(103, 3); // delta 3 < marges combinées (6)
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.NoMeasurableGain");
    }

    [Fact]
    public void Regression_is_not_a_gain()
    {
        var v = AbComparer.Compare(Bench(110, 1), Bench(100, 1));
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.Regression");
    }

    [Fact]
    public void Verdict_carries_both_benchmarks()
    {
        var before = Bench(100, 1); var after = Bench(110, 1);
        var v = AbComparer.Compare(before, after);
        v.Before.Should().BeSameAs(before);
        v.After.Should().BeSameAs(after);
    }

    // --- La régularité des images (1 % bas) est le critère principal ---
    // Un levier d'efficacité (undervolt GPU, limite de puissance, courbe de ventilation) vaut ~0 sur la
    // moyenne par construction ; sa valeur est des 1 % bas plus réguliers et des températures. Le verdict
    // ne doit donc pas reposer sur la seule moyenne.

    [Fact]
    public void Gain_when_one_percent_lows_improve_beyond_margin_even_if_avg_flat()
    {
        // moyenne plate dans la marge, mais 1 % bas nettement en hausse → le gain de l'undervolt : gardé, gain.
        var before = Bench(avg: 100, p1Low: 70, moe: 1);
        var after = Bench(avg: 100, p1Low: 80, moe: 1); // delta moyenne 0 ; delta 1 % bas 10 > 2
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeTrue();
        v.SummaryKey.Should().Be("Scan.Ab.MeasurableGain");
    }

    [Fact]
    public void No_gain_when_avg_and_one_percent_lows_both_within_margin()
    {
        // L'issue attendue d'un levier d'efficacité : moyenne tenue, 1 % bas tenus.
        // Pas une régression → doit être gardé (pas de retour automatique).
        var before = Bench(avg: 100, p1Low: 70, moe: 2);
        var after = Bench(avg: 101, p1Low: 71, moe: 2); // les deux deltas (1) < marge combinée (4)
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.NoMeasurableGain");
    }

    [Fact]
    public void Regression_when_one_percent_lows_worse_beyond_margin_even_if_avg_flat()
    {
        // moyenne plate, mais régularité dégradée au-delà de la marge → régression (retour automatique).
        // C'est la régression des 1 % bas par jeu (ReBAR, undervolt) que la règle sur la seule moyenne manquait.
        var before = Bench(avg: 100, p1Low: 80, moe: 1);
        var after = Bench(avg: 100, p1Low: 70, moe: 1); // delta moyenne 0 ; delta 1 % bas -10
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.Regression");
    }

    [Fact]
    public void Regression_when_avg_clearly_worse_beyond_margin()
    {
        // moyenne nettement pire (1 % bas stables) → toujours une régression sur le signal moyen.
        var before = Bench(avg: 110, p1Low: 80, moe: 1);
        var after = Bench(avg: 100, p1Low: 80, moe: 1); // delta moyenne -10 ; 1 % bas plats
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.Regression");
    }

    [Fact]
    public void Regression_takes_priority_when_lows_drop_even_if_avg_rises_within_margin()
    {
        // la moyenne monte un peu dans la marge, mais les 1 % bas s'effondrent au-delà → la perte de
        // régularité l'emporte : régression, jamais vendue comme un gain.
        var before = Bench(avg: 100, p1Low: 80, moe: 1);
        var after = Bench(avg: 101, p1Low: 70, moe: 1); // moyenne +1 (dans la marge) ; 1 % bas -10
        var v = AbComparer.Compare(before, after);
        v.IsMeasurableGain.Should().BeFalse();
        v.SummaryKey.Should().Be("Scan.Ab.Regression");
    }
}
