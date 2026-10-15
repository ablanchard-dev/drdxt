using FluentAssertions;

namespace DrDxt.Measurement.Tests;

public class FrameSeriesHygieneTests
{
    // n images de `ms` chacune : une seconde de jeu à 1000/ms fps.
    private static IEnumerable<double> Seconds(double seconds, double ms)
        => Enumerable.Repeat(ms, (int)Math.Round(seconds * 1000.0 / ms));

    [Fact]
    public void Drops_the_warmup_seconds()
    {
        // 10 s d'images au rythme d'un chargement puis 100 s de jeu, échauffement coupé à 10 s.
        var series = Seconds(10, 50).Concat(Seconds(100, 7)).ToList();

        var r = FrameSeriesHygiene.Clean(series, warmupSeconds: 10, outOfFocusMedianMs: 1000);

        r.Applied.Should().BeTrue();
        r.WarmupSecondsDropped.Should().Be(10);
        r.FrameTimesMs.Should().OnlyContain(x => x == 7);
    }

    [Fact]
    public void Drops_the_out_of_focus_seconds_by_their_median()
    {
        // 60 s de jeu, puis 20 s en alt-tab au plafond de 30 fps hors focus, puis 60 s de plus.
        var series = Seconds(60, 7).Concat(Seconds(20, 33.3)).Concat(Seconds(60, 7)).ToList();

        var r = FrameSeriesHygiene.Clean(series, warmupSeconds: 0);

        r.Applied.Should().BeTrue();
        r.OutOfFocusSecondsDropped.Should().BeInRange(19, 21);
        // Seules les deux secondes de frontière peuvent survivre : elles mélangent images de jeu et images
        // plafonnées, donc leur médiane reste sous le seuil. Tout l'intérieur de l'alt-tab disparaît.
        // Filtrer par secondes entières ne peut pas faire mieux, et prétendre le contraire serait
        // le mensonge que ce filtre existe pour éviter.
        r.FrameTimesMs.Count(x => x > 25).Should().BeLessThan(70);
        r.FrameTimesMs.Count(x => x == 7).Should().BeGreaterThan(16_000);
    }

    [Fact]
    public void A_single_stutter_does_not_delete_its_second()
    {
        // Un accroc de 200 ms dans une seconde par ailleurs bonne : la médiane reste à 7 ms, donc la
        // seconde survit ; le filtre retire le hors-jeu, jamais les vraies saccades.
        var oneBadSecond = Seconds(1, 7).Concat(new[] { 200.0 });
        var series = Seconds(30, 7).Concat(oneBadSecond).Concat(Seconds(30, 7)).ToList();

        var r = FrameSeriesHygiene.Clean(series, warmupSeconds: 0);

        r.Applied.Should().BeTrue();
        r.OutOfFocusSecondsDropped.Should().Be(0);
        r.FrameTimesMs.Should().Contain(200.0);
    }

    [Fact]
    public void Refuses_rather_than_emptying_a_genuinely_slow_session()
    {
        // Une machine qui tourne vraiment à 30 fps : chaque seconde est au-dessus du seuil. Supprimer
        // toute la capture transformerait un vrai problème en résultat vide.
        var series = Seconds(300, 33.3).ToList();

        var r = FrameSeriesHygiene.Clean(series, warmupSeconds: 0);

        r.Applied.Should().BeFalse();
        r.FrameTimesMs.Should().BeEquivalentTo(series);
    }

    [Fact]
    public void Slices_are_equal_duration_and_the_remainder_is_dropped()
    {
        var series = Seconds(100, 10).ToList(); // 100 s

        var slices = FrameSeriesHygiene.Slice(series, sliceSeconds: 30);

        slices.Should().HaveCount(3);                       // 90 s utilisées, reste de 10 s écarté
        slices.Should().OnlyContain(s => s.Count == 3000);  // 30 s / 10 ms
    }

    [Fact]
    public void Slice_returns_nothing_when_the_session_is_shorter_than_one_slice()
        => FrameSeriesHygiene.Slice(Seconds(20, 10).ToList(), sliceSeconds: 30).Should().BeEmpty();

    [Fact]
    public void Slices_feed_the_inter_run_margin_from_a_single_session()
    {
        // Le bénéfice : une seule longue session donne les >= 3 agrégats dont InterRunMarginOfError a
        // besoin, au lieu d'exiger trois passes de banc séparées.
        var series = Seconds(300, 10).ToList(); // 30 000 images = exactement 5 x 60 s

        var perSlice = FrameSeriesHygiene.Slice(series, 60)
            .Select(s => FrametimeStatistics.AvgFps(s))
            .ToList();

        perSlice.Should().HaveCount(5);
        FrametimeStatistics.InterRunMarginOfError(perSlice).Should().NotBe(double.PositiveInfinity);
    }

    // --- l'échauffement est mesuré, pas supposé (régression vue sur une vraie capture) ---

    [Fact]
    public void Detects_no_warmup_when_the_capture_starts_in_gameplay()
    {
        // La vraie capture de 178 s : chaque seconde déjà au rythme du jeu. Couper 90 s à plat
        // jetait la moitié des images pour un écart de 0,9 % et privait Slice d'une seule tranche de 120 s.
        var series = Seconds(178, 7.0).ToList();

        var r = FrameSeriesHygiene.Clean(series);

        r.Applied.Should().BeTrue();
        r.WarmupSecondsDropped.Should().Be(0);
        r.FrameTimesMs.Count.Should().Be(series.Count);
        FrameSeriesHygiene.Slice(r.FrameTimesMs, 120.0).Should().HaveCount(1,
            "178 s of kept gameplay yields one 120 s slice; a flat 90 s warm-up yielded none");
    }

    [Fact]
    public void Drops_a_real_loading_head_and_stops_at_the_first_playable_second()
    {
        // 12 s de chargement à 30 fps, puis du jeu à ~143 fps.
        var series = Seconds(12, 33.3).Concat(Seconds(100, 7.0)).ToList();

        var r = FrameSeriesHygiene.Clean(series);

        r.Applied.Should().BeTrue();
        r.WarmupSecondsDropped.Should().Be(12);
        r.FrameTimesMs.Should().OnlyContain(x => x == 7.0);
    }

    [Fact]
    public void A_stutter_after_the_start_is_never_swallowed_as_warmup()
    {
        // Un défaut à signaler, pas un échauffement à cacher : le balayage s'arrête à la première seconde normale.
        var series = Seconds(30, 7.0).Concat(Seconds(3, 40.0)).Concat(Seconds(30, 7.0)).ToList();

        var r = FrameSeriesHygiene.Clean(series);

        r.WarmupSecondsDropped.Should().Be(0);
        r.OutOfFocusSecondsDropped.Should().Be(3, "40 ms seconds are still caught by the focus rule");
    }

    [Fact]
    public void Detected_warmup_never_exceeds_the_cap()
    {
        var series = Seconds(40, 33.3).Concat(Seconds(60, 7.0)).ToList();

        var r = FrameSeriesHygiene.Clean(series, warmupSeconds: 10.0);

        r.WarmupSecondsDropped.Should().Be(10, "the knob is an upper bound on the search");
    }
}
