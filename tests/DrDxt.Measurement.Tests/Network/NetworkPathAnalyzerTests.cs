using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Network;

// -----------------------------------------------------------------------
// Attribution de l'instabilité.
//
// Le verdict ne repose sur aucune constante : seulement sur le fait que le premier saut est
// contenu dans le chemin complet. Ces tests défendent surtout le refus de trancher : sans
// jitter des deux côtés, il n'y a rien à comparer, et deviner serait pire que se taire.
// -----------------------------------------------------------------------
public sealed class NetworkPathAnalyzerTests
{
    private static NetworkLatencyReport Report(double? jitter, string target = "x") =>
        new(target, Sent: 10, Received: jitter is null ? 0 : 10,
            LossPercent: jitter is null ? 100 : 0,
            AvgMs: jitter is null ? null : 10, MinMs: jitter is null ? null : 8,
            MaxMs: jitter is null ? null : 14, P95Ms: jitter is null ? null : 13,
            JitterMs: jitter);

    [Fact]
    public void Instability_already_present_at_the_first_hop_is_attributed_there()
    {
        // Le premier saut est contenu dans le chemin complet : si son jitter égale ou dépasse
        // celui du chemin entier, le trajet au-delà n'ajoute rien de mesurable.
        var r = NetworkPathAnalyzer.Compare(Report(5.5), Report(1.95));

        r.Verdict.Should().Be(NetworkPathVerdict.StartsAtFirstHop);
    }

    // Une mutation qui rendait « indiscernable » un écart d'exactement une milliseconde restait
    // verte. La sonde résout la milliseconde : un écart de 1 ms se départage, un écart de 0,9 ms non.
    [Fact]
    public void Un_ecart_d_exactement_la_resolution_de_la_sonde_se_departage()
    {
        NetworkPathAnalyzer.Compare(Report(3.0), Report(2.0)).Verdict.Should().Be(NetworkPathVerdict.StartsAtFirstHop);
        NetworkPathAnalyzer.Compare(Report(2.9), Report(2.0)).Verdict.Should().Be(NetworkPathVerdict.Indistinguishable);
    }

    [Fact]
    public void Equal_jitter_names_nobody()
    {
        // Ce test garantissait l'inverse (« égalité => premier saut »), au motif que tout le
        // jitter du chemin serait déjà là au premier saut. Le raisonnement suppose une précision
        // que la sonde n'a pas : Ping ne rend que des millisecondes entières, et les deux
        // fenêtres sont deux échantillons distincts. Deux mesures égales veulent dire « on ne
        // sait pas les départager », pas « c'est le lien local ».
        NetworkPathAnalyzer.Compare(Report(3.0), Report(3.0))
            .Verdict.Should().Be(NetworkPathVerdict.Indistinguishable);
    }

    [Fact]
    public void A_clean_link_is_not_handed_a_culprit()
    {
        // Mesuré sur la machine de reference : 1er saut 0,09 ms contre 0 ms sur internet — un lien
        // propre — et l'outil annonçait « l'instabilité commence sur le lien local ». Sur quatre
        // passages consécutifs, sans que rien ne change, le verdict basculait d'un bord à l'autre.
        NetworkPathAnalyzer.Compare(Report(0.09), Report(0.0))
            .Verdict.Should().Be(NetworkPathVerdict.Indistinguishable);
        NetworkPathAnalyzer.Compare(Report(0.0), Report(0.36))
            .Verdict.Should().Be(NetworkPathVerdict.Indistinguishable);
        NetworkPathAnalyzer.Compare(Report(2.64), Report(2.45))
            .Verdict.Should().Be(NetworkPathVerdict.Indistinguishable);
    }

    [Fact]
    public void A_difference_the_probe_can_actually_resolve_is_still_attributed()
    {
        // La neutralité ne doit pas avaler un vrai écart : au-delà de la résolution de la sonde,
        // le verdict tranche toujours, dans les deux sens.
        NetworkPathAnalyzer.Compare(Report(4.0), Report(2.5))
            .Verdict.Should().Be(NetworkPathVerdict.StartsAtFirstHop);
        NetworkPathAnalyzer.Compare(Report(2.5), Report(4.0))
            .Verdict.Should().Be(NetworkPathVerdict.AppearsBeyondGateway);
    }

    [Fact]
    public void A_calm_first_hop_with_a_noisy_path_points_beyond_the_gateway()
    {
        NetworkPathAnalyzer.Compare(Report(0.5), Report(12.0))
            .Verdict.Should().Be(NetworkPathVerdict.AppearsBeyondGateway);
    }

    [Fact]
    public void Refuses_to_judge_when_either_side_has_no_jitter()
    {
        // Une passerelle qui ne répond pas à l'ICMP (beaucoup de box le filtrent) ne veut pas
        // dire « lien local sain ». Sans donnée, pas de verdict.
        NetworkPathAnalyzer.Compare(Report(null), Report(2.0))
            .Verdict.Should().Be(NetworkPathVerdict.Unknown);
        NetworkPathAnalyzer.Compare(Report(2.0), Report(null))
            .Verdict.Should().Be(NetworkPathVerdict.Unknown);
        NetworkPathAnalyzer.Compare(Report(null), Report(null))
            .Verdict.Should().Be(NetworkPathVerdict.Unknown);
    }

    [Fact]
    public void The_report_keeps_both_windows_and_carries_its_caveat()
    {
        var hop = Report(5.5, "192.168.1.254");
        var net = Report(1.95, "1.1.1.1");

        var r = NetworkPathAnalyzer.Compare(hop, net);

        // Les deux mesures restent lisibles : un verdict sans ses chiffres n'est pas auditable.
        r.FirstHop.Target.Should().Be("192.168.1.254");
        r.Internet.Target.Should().Be("1.1.1.1");
        // Une box répond à l'ICMP avec son propre CPU : le verdict est une piste, pas une preuve.
        r.CaveatKey.Should().Be("measure.network.first_hop_caveat");
    }
}
