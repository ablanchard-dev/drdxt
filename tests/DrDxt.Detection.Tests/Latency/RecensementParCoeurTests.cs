using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Latency;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Latency;

// Les interruptions et les DPC par cœur, sur tous les groupes (valeurs fictives).
public sealed class RecensementParCoeurTests
{
    private static RecensementParCoeur.Brut B(ulong it, ulong dpc, ulong tDpc, ulong tIt, ulong t)
        => new(it, dpc, tDpc, tIt, t * 10_000_000, t * 10_000_000, 10_000_000);

    [Fact]
    public void Deux_releves_donnent_les_taux_par_seconde_et_la_part_du_temps()
    {
        var c = RecensementParCoeur.Calculer(new Dictionary<string, RecensementParCoeur.Brut> { ["0,3"] = B(100, 50, 0, 0, 10) },
            new Dictionary<string, RecensementParCoeur.Brut> { ["0,3"] = B(10_530, 8_729, 2_000_000, 1_000_000, 11) }).Single();
        (c.InterruptionsParSeconde, c.DpcParSeconde).Should().Be((10_430d, 8_679d));
        c.Part.Should().BeApproximately(0.30, 1e-9);
    }

    // Notre règle : tous les groupes sont lus ; un cœur du groupe 1 compte comme les autres.
    [Fact]
    public void Un_coeur_du_second_groupe_est_recense()
    {
        var a = new Dictionary<string, RecensementParCoeur.Brut> { ["0,0"] = B(0, 0, 0, 0, 10), ["1,5"] = B(0, 0, 0, 0, 10) };
        var b = new Dictionary<string, RecensementParCoeur.Brut> { ["0,0"] = B(10, 1, 0, 0, 11), ["1,5"] = B(10, 1, 4_000_000, 0, 11) };
        RecensementParCoeur.Juger(RecensementParCoeur.Calculer(a, b)).ValeurCourante.Should().StartWith("cœur 1,5");
    }

    // Notre règle : le constat dit le cœur confisqué, il ne propose jamais d'épingler.
    [Fact]
    public void Un_coeur_confisque_est_dit_sans_jamais_proposer_d_epingler()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var coeurs = new List<RecensementParCoeur.Coeur> { new("0,13", 10_430, 8_073, 0.30), new("0,0", 500, 100, 0.01), new("0,1", 400, 90, 0.02) };
        var c = RecensementParCoeur.Juger(coeurs);
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().Contain("0,13").And.Contain("n'épingle jamais rien");
    }

    [Fact]
    public void Une_charge_repartie_n_est_qu_observee_et_rien_de_lu_est_non_lu()
    {
        RecensementParCoeur.Juger([new("0,0", 1, 1, 0.30), new("0,1", 1, 1, 0.29)]).Statut.Should().Be(RepairStatus.Observed);
        RecensementParCoeur.Juger(null).Statut.Should().Be(RepairStatus.Unknown);
    }
}
