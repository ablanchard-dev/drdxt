using DrDxt.Core;
using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Network;

// -----------------------------------------------------------------------
// Instrument réseau. Les tests qui comptent ici sont ceux d'honnêteté : cette classe de défaut
// (un nombre plausible là où il n'y a pas de mesure) est la plus coûteuse ; un rapport « 0 ms, 0 %
// de perte » d'une connexion qui n'a jamais répondu se lirait comme un lien parfait.
// Pur + fausse sonde : aucun accès réseau, déterministe.
// -----------------------------------------------------------------------
public sealed class NetworkLatencyStatisticsTests
{
    private static NetworkProbeSample Reply(double ms) => new(ms);
    private static NetworkProbeSample Miss() => new((double?)null);

    [Fact]
    public void Zero_replies_reports_loss_and_UNKNOWN_latency_never_zero_ms()
    {
        var r = NetworkLatencyStatistics.Summarize("host", new[] { Miss(), Miss(), Miss() });

        r.Sent.Should().Be(3);
        r.Received.Should().Be(0);
        r.LossPercent.Should().Be(100);
        r.HasLatency.Should().BeFalse();
        // Tout l'enjeu : aucun 0 ms fabriqué sur un lien mort.
        r.AvgMs.Should().BeNull();
        r.MinMs.Should().BeNull();
        r.MaxMs.Should().BeNull();
        r.P95Ms.Should().BeNull();
        r.JitterMs.Should().BeNull();
    }

    [Fact]
    public void Loss_is_measured_against_what_was_actually_sent()
    {
        var r = NetworkLatencyStatistics.Summarize("host", new[] { Reply(20), Miss(), Reply(30), Miss() });

        r.Sent.Should().Be(4);
        r.Received.Should().Be(2);
        r.LossPercent.Should().Be(50);
        r.AvgMs.Should().Be(25);
    }

    [Fact]
    public void A_lost_probe_is_not_counted_as_a_huge_latency_sample()
    {
        // Un raté ne doit jamais fuir dans min/max/moy comme une lecture de la taille du délai.
        var r = NetworkLatencyStatistics.Summarize("host", new[] { Reply(18), Miss(), Reply(22) });

        r.MaxMs.Should().Be(22, "the only samples are the replies");
        r.MinMs.Should().Be(18);
        r.AvgMs.Should().Be(20);
    }

    [Fact]
    public void Jitter_is_the_mean_absolute_delta_between_consecutive_replies()
    {
        // 20 → 26 → 24 : deltas 6 et 2 → moyenne 4.
        var r = NetworkLatencyStatistics.Summarize("host", new[] { Reply(20), Reply(26), Reply(24) });

        r.JitterMs.Should().BeApproximately(4, 1e-9);
    }

    [Fact]
    public void Jitter_skips_pairs_broken_by_a_lost_probe_instead_of_inventing_a_delta()
    {
        // 20, raté, 60, 62 : la seule paire consécutive intacte est 60 → 62 → gigue 2.
        // Compter l'écart 20 → 60 par-dessus un trou annoncerait un saut de 40 ms que personne n'a mesuré.
        var r = NetworkLatencyStatistics.Summarize("host", new[] { Reply(20), Miss(), Reply(60), Reply(62) });

        r.JitterMs.Should().BeApproximately(2, 1e-9);
    }

    [Fact]
    public void Jitter_is_unknown_with_fewer_than_two_consecutive_replies()
    {
        NetworkLatencyStatistics.Summarize("host", new[] { Reply(20) }).JitterMs.Should().BeNull();
        NetworkLatencyStatistics.Summarize("host", new[] { Reply(20), Miss(), Reply(20) }).JitterMs.Should().BeNull();
    }

    [Fact]
    public void P95_returns_a_value_that_was_actually_measured()
    {
        var samples = new[] { Reply(10), Reply(11), Reply(12), Reply(13), Reply(90) };

        var r = NetworkLatencyStatistics.Summarize("host", samples);

        r.P95Ms.Should().Be(90, "nearest-rank picks a real sample, it never interpolates");
        r.AvgMs.Should().BeApproximately(27.2, 1e-9);
    }

    [Fact]
    public void Non_finite_or_negative_readings_are_treated_as_misses_not_as_data()
    {
        var r = NetworkLatencyStatistics.Summarize("host",
            new[] { Reply(double.NaN), Reply(-5), Reply(double.PositiveInfinity), Reply(30) });

        r.Received.Should().Be(1);
        r.AvgMs.Should().Be(30);
        r.LossPercent.Should().Be(75);
    }

    [Fact]
    public void Report_carries_its_target_and_its_caveat_key()
    {
        var r = NetworkLatencyStatistics.Summarize("1.1.1.1", new[] { Reply(12) });

        r.Target.Should().Be("1.1.1.1");
        // Les nombres ne doivent jamais voyager sans « ICMP vers un hôte, pas le serveur du jeu ».
        r.CaveatKey.Should().Be("measure.network.icmp_caveat");
    }
}

public sealed class NetworkLatencyMeterTests
{
    private sealed class FakeProbe : INetworkProbe
    {
        private readonly Queue<double?> _replies;
        public int Calls { get; private set; }
        public bool Throw { get; init; }
        public FakeProbe(params double?[] replies) => _replies = new Queue<double?>(replies);

        public Task<NetworkProbeSample> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("probe blew up");
            return Task.FromResult(new NetworkProbeSample(_replies.Count > 0 ? _replies.Dequeue() : null));
        }
    }

    private static readonly TimeSpan NoWait = TimeSpan.Zero;
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Measures_the_requested_number_of_probes_and_aggregates_them()
    {
        var probe = new FakeProbe(20, 24, 22);
        var meter = new NetworkLatencyMeter(probe);

        var r = await meter.MeasureAsync("host", 3, NoWait, OneSecond, CancellationToken.None, warmupProbes: 0);

        r.IsOk.Should().BeTrue();
        probe.Calls.Should().Be(3);
        r.Value!.Received.Should().Be(3);
        r.Value.AvgMs.Should().Be(22);
    }

    [Fact]
    public async Task Warmup_probes_are_sent_and_EXCLUDED_from_the_report()
    {
        // La chauffe existe parce que la premiere fenetre d'un processus mesure son propre
        // demarrage (verifie sur machine reelle : 1re fenetre jitter 0,95 / pointe a 11 ms,
        // fenetres suivantes 0,21 — meme cible, meme seconde). Si ses echantillons entraient
        // dans le rapport, la chauffe ne servirait a rien.
        var probe = new FakeProbe(999, 999, 999, 20, 20, 20);   // 3 jetees, puis 3 mesurees
        var meter = new NetworkLatencyMeter(probe);

        var r = await meter.MeasureAsync("host", 3, NoWait, OneSecond, CancellationToken.None, warmupProbes: 3);

        probe.Calls.Should().Be(6, "3 de chauffe + 3 mesurees");
        r.Value!.Received.Should().Be(3);
        r.Value.AvgMs.Should().Be(20, "les 999 de chauffe ne doivent PAS entrer dans la moyenne");
        r.Value.MaxMs.Should().Be(20);
    }

    [Fact]
    public async Task A_failing_warmup_does_not_fail_the_measurement()
    {
        // La chauffe est une preparation, pas une observation : si elle rate, on mesure quand meme.
        var probe = new FakeProbe(null, null, 20, 20);
        var meter = new NetworkLatencyMeter(probe);

        var r = await meter.MeasureAsync("host", 2, NoWait, OneSecond, CancellationToken.None, warmupProbes: 2);

        r.IsOk.Should().BeTrue();
        r.Value!.AvgMs.Should().Be(20);
    }

    [Fact]
    public async Task A_throwing_probe_is_a_miss_not_a_crash()
    {
        var meter = new NetworkLatencyMeter(new FakeProbe(20) { Throw = true });

        var r = await meter.MeasureAsync("host", 2, NoWait, OneSecond, CancellationToken.None, warmupProbes: 0);

        r.IsOk.Should().BeTrue();
        r.Value!.HasLatency.Should().BeFalse();
        r.Value.LossPercent.Should().Be(100);
    }

    [Fact]
    public async Task Cancelled_before_the_first_probe_is_NO_measurement_not_100_percent_loss()
    {
        var probe = new FakeProbe(20);
        var meter = new NetworkLatencyMeter(probe);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = await meter.MeasureAsync("host", 5, NoWait, OneSecond, cts.Token, warmupProbes: 0);

        // Annoncer « 100 % de perte » pour une fenêtre jamais envoyée serait un verdict fabriqué.
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.MeasureNoSamples);
        probe.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("   ", 5)]
    [InlineData("host", 0)]
    [InlineData("host", -3)]
    public async Task Refuses_an_unusable_request_before_sending_anything(string host, int count)
    {
        var probe = new FakeProbe(20);

        var r = await new NetworkLatencyMeter(probe).MeasureAsync(host, count, NoWait, OneSecond, CancellationToken.None, warmupProbes: 0);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.MeasureInvalidInput);
        probe.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Refuses_a_non_positive_timeout_before_sending_anything()
    {
        var probe = new FakeProbe(20);

        var r = await new NetworkLatencyMeter(probe).MeasureAsync("host", 3, NoWait, TimeSpan.Zero, CancellationToken.None, warmupProbes: 0);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.MeasureInvalidInput);
        probe.Calls.Should().Be(0);
    }
}
