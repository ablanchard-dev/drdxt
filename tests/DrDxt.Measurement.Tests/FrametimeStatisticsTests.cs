using DrDxt.Measurement;
using FluentAssertions;
using Xunit;

namespace DrDxt.Measurement.Tests;

public class FrametimeStatisticsTests
{
    // Composé = 4, 5, 6 ; le retournement indépendant (3) et par superposition (8) ne l'est pas ; inconnu (0) écarté.
    [Fact]
    public void La_part_composee_compte_les_recopies_sur_les_modes_connus()
    {
        FrametimeStatistics.PartComposee([3, 4, 0, 8, 5, 6, 0, 3]).Should().Be(0.5);
        FrametimeStatistics.PartComposee([0, 0]).Should().BeNull();
        FrametimeStatistics.PartComposee(null).Should().BeNull();
    }

    [Fact]
    public void Avg_fps_of_constant_16_67ms_is_60()
    {
        var ft = Enumerable.Repeat(16.6667, 1000).ToList();
        FrametimeStatistics.AvgFps(ft).Should().BeApproximately(60.0, 0.1);
    }

    [Fact]
    public void P1_low_is_1st_percentile_of_frametimes_as_fps()
    {
        // 99 frames à 10ms (100 fps) + 1 frame à 100ms (10 fps).
        var ft = Enumerable.Repeat(10.0, 99).Append(100.0).ToList();
        // 1% low = moyenne du pire 1% des FRAMES (la frame à 100ms) → 10 fps.
        FrametimeStatistics.P1LowFps(ft).Should().BeApproximately(10.0, 0.5);
    }

    [Fact]
    public void P01_low_is_0_1_percentile()
    {
        var ft = Enumerable.Repeat(10.0, 999).Append(200.0).ToList();
        FrametimeStatistics.P01LowFps(ft).Should().BeApproximately(5.0, 0.5);
    }

    [Fact]
    public void Empty_frametimes_yield_zero_not_nan()
    {
        FrametimeStatistics.AvgFps(new List<double>()).Should().Be(0);
        FrametimeStatistics.P1LowFps(new List<double>()).Should().Be(0);
    }

    // --- Marge inter-passes : la marge A/B honnête est l'écart entre passes,
    //     pas un IC par image (les temps d'image sont autocorrélés → par image est bien trop serré). ---

    [Fact]
    public void Inter_run_margin_is_infinite_below_three_runs()
    {
        // Moins de 3 passes indépendantes → la variance entre passes ne peut pas être bornée → « gain non prouvable ».
        FrametimeStatistics.InterRunMarginOfError(new List<double> { 60 }).Should().Be(double.PositiveInfinity);
        FrametimeStatistics.InterRunMarginOfError(new List<double> { 60, 61 }).Should().Be(double.PositiveInfinity);
        FrametimeStatistics.InterRunMarginOfError(new List<double>()).Should().Be(double.PositiveInfinity);
    }

    [Fact]
    public void Inter_run_margin_zero_for_identical_run_means()
        => FrametimeStatistics.InterRunMarginOfError(new List<double> { 60, 60, 60 }).Should().BeApproximately(0, 1e-9);

    [Fact]
    public void Inter_run_margin_positive_and_finite_for_varying_run_means()
    {
        var m = FrametimeStatistics.InterRunMarginOfError(new List<double> { 55, 60, 65 });
        m.Should().BeGreaterThan(0);
        double.IsFinite(m).Should().BeTrue();
    }

    [Fact]
    public void Inter_run_margin_widens_with_more_run_to_run_spread()
    {
        var tight = FrametimeStatistics.InterRunMarginOfError(new List<double> { 59, 60, 61 });
        var wide = FrametimeStatistics.InterRunMarginOfError(new List<double> { 40, 60, 80 });
        wide.Should().BeGreaterThan(tight);
    }

    [Fact]
    public void Mean_or_null_is_null_when_no_valid_values()
    {
        FrametimeStatistics.MeanOrNull(new List<double>()).Should().BeNull();
        FrametimeStatistics.MeanOrNull(new List<double> { 0, -1, double.NaN, double.PositiveInfinity }).Should().BeNull();
    }

    [Fact]
    public void Mean_or_null_averages_only_valid_values()
        => FrametimeStatistics.MeanOrNull(new List<double> { 2, 4, 0, -1 })!.Value.Should().BeApproximately(3.0, 1e-9);

    [Fact]
    public void Ignores_zero_and_negative_frametimes()
    {
        // Une frame droppée (0ms) ou aberrante (<0) ne doit pas gonfler l'AvgFPS :
        // seules les frames valides comptent (défense en profondeur vs glitch PresentMon).
        var clean = Enumerable.Repeat(10.0, 100).ToList();                 // 100 fps
        var dirty = new List<double> { 0.0, -5.0 }.Concat(clean).ToList();

        FrametimeStatistics.AvgFps(dirty).Should().BeApproximately(100.0, 0.1);
        FrametimeStatistics.AvgFps(dirty).Should().BeApproximately(FrametimeStatistics.AvgFps(clean), 1e-6);
    }

    // ── La part du temps total, appariee ───────────────────────────────────────────────────
    // Le defaut qu'elle repare : l'occupation GPU du produit — celle qui porte le verdict de
    // goulot — se calculait « moyenne du haut ÷ moyenne du bas », deux moyennes qui ne portaient
    // pas forcement sur les memes trames.

    [Fact]
    public void Un_ZERO_compte_dans_la_part_au_lieu_de_disparaitre()
    {
        // Une trame ou le GPU n'a rien fait est une mesure valide. La moyenne des GPU-busy
        // l'ecartait (elle ne garde que le strictement positif) alors que son temps de trame
        // restait au denominateur : l'occupation en sortait GONFLEE, donc le plafond RETRECI.
        var busy = new List<double> { 0.0, 5.0, 5.0, 5.0 };
        var trames = new List<double> { 10.0, 10.0, 10.0, 10.0 };

        FrametimeStatistics.SumRatioOrNull(busy, trames).Should().BeApproximately(0.375, 1e-9);

        // Ce que rendait l'ancienne forme, pour memoire : 5 / 10 = 50 % au lieu de 37,5 %.
        var ancienne = FrametimeStatistics.MeanOrNull(busy)!.Value / trames.Average();
        ancienne.Should().BeApproximately(0.50, 1e-9);
    }

    [Fact]
    public void Deux_series_DESYNCHRONISEES_ne_produisent_aucune_part()
    {
        // Melanger deux populations rend un chiffre credible et faux : mieux vaut null.
        FrametimeStatistics.SumRatioOrNull([5.0, 5.0], [10.0, 10.0, 10.0]).Should().BeNull();
        FrametimeStatistics.SumRatioOrNull([], []).Should().BeNull();
        FrametimeStatistics.SumRatioOrNull(null, [10.0]).Should().BeNull();
    }

    [Fact]
    public void Une_trame_inexploitable_sort_des_DEUX_cotes_a_la_fois()
    {
        // Denominateur nul, numerateur non fini : la paire entiere est ecartee, jamais un seul
        // des deux cotes — c'est precisement ce decalage qui faussait le rapport.
        var busy = new List<double> { 5.0, double.NaN, 5.0 };
        var trames = new List<double> { 10.0, 10.0, 0.0 };

        FrametimeStatistics.SumRatioOrNull(busy, trames).Should().BeApproximately(0.5, 1e-9);
    }

    // Un gel en pleine partie (compilation de shaders, pilote qui décroche), même de plusieurs secondes, reste une image :
    // c'est lui que le 1 % low et le 0,1 % low doivent montrer. Seul le relevé, qui sait si le jeu avait perdu le premier
    // plan, écarte une pause (ReleveDesParties).
    [Fact]
    public void Un_gel_de_plusieurs_secondes_reste_compte()
    {
        var images = Enumerable.Repeat(10.0, 99).Append(2_500.0).ToList();

        FrametimeStatistics.P1LowFps(images).Should().BeApproximately(0.4, 0.01);
    }
}
