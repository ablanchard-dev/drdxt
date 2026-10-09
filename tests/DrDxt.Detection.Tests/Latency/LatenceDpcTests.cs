using DrDxt.Core;
using DrDxt.Detection.Latency;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Latency;

/// <summary>Branchement DPC — le releve des 20 s en jeu devient un constat de sante, jamais « sain »
/// par defaut.</summary>
public class LatenceDpcTests
{
    private static DpcTraceResult Trace(DpcOutcome issue, params DpcCost[] couts) =>
        new(issue, 0, couts, new Dictionary<byte, int>(), 1000, 20, 0, 0);

    [Fact]
    public void Aucun_pic_au_dela_d_une_ms_est_un_constat_verifie()
        => LatenceDpc.Classer(Trace(DpcOutcome.Ok, new DpcCost("ndis.sys", 500, 12, 0.371, 0)))
            .Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Un_pic_d_une_ms_est_suspect_et_nomme_le_pilote()
    {
        var c = LatenceDpc.Classer(Trace(DpcOutcome.Ok, new DpcCost("nvlddmkm.sys", 900, 40, 1.0, 1)));

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Contain("nvlddmkm.sys");
    }

    [Fact]
    public void Le_cadre_KMDF_n_est_jamais_designe_comme_le_pilote_a_changer()
        => LatenceDpc.Classer(Trace(DpcOutcome.Ok, new DpcCost("Wdf01000.sys", 12946, 355, 5.807, 1)))
            .Explication.Should().Contain("pas le fautif").And.NotContain("Le remède est le pilote de Wdf01000");

    [Fact]
    public void Sans_les_droits_la_latence_n_est_pas_lue()
        => LatenceDpc.Classer(Trace(DpcOutcome.DroitsInsuffisants)).Statut.Should().Be(RepairStatus.Unknown);

    [Fact]
    public void Des_evenements_sans_duree_decodee_ne_valent_pas_une_machine_saine()
        => LatenceDpc.Classer(Trace(DpcOutcome.Ok)).Statut.Should().Be(RepairStatus.Unknown);
}
