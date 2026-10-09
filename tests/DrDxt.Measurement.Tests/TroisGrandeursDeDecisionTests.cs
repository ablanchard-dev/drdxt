using DrDxt.Core;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// La grandeur qu'on perd sans que rien ne rougisse.
///
/// `BenchmarkResult` porte ses grandeurs optionnelles en queue, avec un `= null` de contrat. Un
/// banc qui le construit EN POSITIONNEL peut donc en oublier une : ca compile, ca tourne, ca ne
/// leve rien. Et en aval `GameMetricSet.Valeur` rend **0** sur un p95 nul, ce que `AutopilotEngine`
/// lit comme « grandeur jamais mesuree » et ecarte du jugement — silencieusement.
///
/// C'est exactement ce qui est arrive au banc de rejeu de `tools/optimize` : il a servi de
/// reference en annoncant trois grandeurs alors qu'il n'en jugeait que deux.
///
/// Et le banc de production n'etait pas couvert non plus. Mesure du 11/09/2026 : remplacer
/// `P95FrametimeMs: FrametimeStatistics.PercentileOrNull(all, 0.95)` par `null` dans
/// `BenchmarkRunner` laissait les **3 725 tests du depot VERTS**. Le code etait juste ; la
/// garantie n'existait pas. Ces deux tests la font exister, des deux cotes.
/// </summary>
public class TroisGrandeursDeDecisionTests
{
    /// <summary>
    /// Une serie qui a une vraie queue, et dont les trois reperes sont DISTINCTS — sinon un test
    /// vert ne dirait pas quel percentile a ete calcule :
    /// 180 trames a 8 ms, 15 a 12 ms, 5 a 20 ms (200 au total).
    /// moyenne 8,6 ms · p95 = 12 ms · p99 = 20 ms.
    /// </summary>
    private static List<double> SerieAQueueConnue()
        => Enumerable.Repeat(8.0, 180)
            .Concat(Enumerable.Repeat(12.0, 15))
            .Concat(Enumerable.Repeat(20.0, 5))
            .ToList();

    private const double P95Attendu = 12.0;

    private static GameMetrics Grandeurs(BenchmarkResult r)
        => new(r.AvgFps, r.P1LowFps, r.P01LowFps, r.P95FrametimeMs);

    private static void AucuneGrandeurNeManque(BenchmarkResult r)
    {
        var m = Grandeurs(r);
        foreach (var k in GameMetricSet.Toutes)
            GameMetricSet.Valeur(m, k).Should().BeGreaterThan(0,
                $"« {GameMetricSet.Libelle(k)} » vaut 0 pour le juge, donc il l'ecarte : "
                + "le banc annoncerait trois grandeurs en n'en jugeant que deux");
    }

    [Fact]
    public async Task Le_banc_de_PRODUCTION_renseigne_les_TROIS_grandeurs_que_le_juge_lit()
    {
        var session = Substitute.For<IPresentMonSession>();
        session.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        session.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Result<FrameCapture>.Ok(new FrameCapture(
                SerieAQueueConnue(), Array.Empty<double>(), Array.Empty<double>())));

        var r = await new BenchmarkRunner(session)
            .RunAsync(1234, TimeSpan.FromMilliseconds(10), 1, default);

        r.IsOk.Should().BeTrue();
        AucuneGrandeurNeManque(r.Value!);

        // Le p95 de cette serie, et pas un autre percentile : 20 ms serait le p99.
        r.Value!.P95FrametimeMs.Should().Be(P95Attendu);
    }

    [Fact]
    public void Une_passe_REJOUEE_renseigne_les_MEMES_trois_grandeurs()
    {
        var r = FrametimeStatistics.PasseDepuisSerie(
            SerieAQueueConnue(), runs: 1, marginOfError: 1.0, hygieneKeptFraction: 1.0);

        AucuneGrandeurNeManque(r);
        r.P95FrametimeMs.Should().Be(P95Attendu);

        // La latence n'est pas dans une serie enregistree : null = pas mesuree, jamais zero.
        r.EstimatedPcLatencyMs.Should().BeNull();
    }

    /// <summary>
    /// Une serie de 1 000 trames dont les quatre reperes sont distincts — c'est ce que la serie a
    /// queue connue ci-dessus ne permet pas : sur 200 trames, `ceil(200 x 1 %) = 2` et
    /// `ceil(200 x 0,1 %) = 1` tombent tous deux dans le meme palier a 20 ms, donc le 1 % low et le
    /// 0,1 % low y valent exactement la meme chose et un test ne peut pas les distinguer.
    ///
    /// 900 trames a 8 ms, 60 a 12 ms, 39 a 20 ms, 1 a 50 ms.
    /// </summary>
    private static List<double> SerieAQuatreReperesDistincts()
        => Enumerable.Repeat(8.0, 900)
            .Concat(Enumerable.Repeat(12.0, 60))
            .Concat(Enumerable.Repeat(20.0, 39))
            .Append(50.0)
            .ToList();

    /// <summary>
    /// « Les formules sont celles de <c>BenchmarkRunner</c> et rien d'autre » : cette phrase est
    /// ecrite dans la doc de <c>PasseDepuisSerie</c>, et le 11/09/2026 elle n'etait garantie que
    /// pour le p95. Mesure : sur la suite complete (3 915 tests), echanger le 1 % low et le 0,1 %
    /// low dans <c>PasseDepuisSerie</c> laissait tout VERT, et biaiser sa moyenne de 10 % aussi.
    /// Seul le p95 rougissait. Le rejeu de <c>tools/optimize</c> sert de reference de mesure : une
    /// derive entre les deux chemins y passerait pour un ecart entre passes.
    ///
    /// Ce test ne pose aucune valeur a la main. Il fait tourner le banc de PRODUCTION et le rejeu
    /// sur la meme serie et exige les memes quatre grandeurs de decision. La distinction prealable
    /// des quatre reperes est verifiee ici meme : sans elle, un echange de deux d'entre eux
    /// passerait pour une egalite.
    /// </summary>
    [Fact]
    public async Task Le_rejeu_calcule_EXACTEMENT_ce_que_le_banc_de_production_calcule()
    {
        var serie = SerieAQuatreReperesDistincts();

        var session = Substitute.For<IPresentMonSession>();
        session.IsServiceAvailable().Returns(Result<bool>.Ok(true));
        session.CaptureAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Result<FrameCapture>.Ok(new FrameCapture(
                serie, Array.Empty<double>(), Array.Empty<double>())));

        var production = await new BenchmarkRunner(session)
            .RunAsync(1234, TimeSpan.FromMilliseconds(10), 1, default);
        production.IsOk.Should().BeTrue();
        var p = production.Value!;

        // La premisse, avant toute comparaison : les quatre reperes sont bien DIFFERENTS, sinon
        // l'egalite ci-dessous serait vraie meme pour un chemin qui en echange deux.
        new[] { p.AvgFps, p.P1LowFps, p.P01LowFps, p.P95FrametimeMs!.Value }
            .Distinct().Should().HaveCount(4,
                "sans quatre reperes distincts, un echange de grandeurs passerait pour une egalite");

        var rejeu = FrametimeStatistics.PasseDepuisSerie(
            serie, runs: 1, marginOfError: 1.0, hygieneKeptFraction: 1.0);

        rejeu.AvgFps.Should().Be(p.AvgFps);
        rejeu.P1LowFps.Should().Be(p.P1LowFps);
        rejeu.P01LowFps.Should().Be(p.P01LowFps);
        rejeu.P95FrametimeMs.Should().Be(p.P95FrametimeMs);
    }
}
