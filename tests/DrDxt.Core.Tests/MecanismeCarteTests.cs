using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>Ce que la limite de puissance fait pendant une partie, lu par le pilote.</summary>
public class MecanismeCarteTests
{
    private static HardwareReading L(double mhz, ClocksEventReason raisons) => new(100, 60, mhz, raisons);

    [Fact]
    public void Les_chiffres_de_la_partie_Warzone_du_08_10_donnent_une_perte_sous_le_bruit()
    {
        // 20 % du temps bridée par la puissance, 2 715 MHz bridée contre 2 730 libre (relevé nvidia-smi du 08/10).
        var lus = Enumerable.Repeat(L(2715, ClocksEventReason.PlafondPuissanceLogiciel), 20)
            .Concat(Enumerable.Repeat(L(2730, ClocksEventReason.Aucune), 80));
        var m = MecanismeCarte.Depuis(lus)!;

        m.PourcentBride.Should().Be(20);
        m.PerteEstimeePourcent.Should().BeApproximately(0.11, 0.01);
    }

    [Fact]
    public void Au_repos_ou_illisible_rien_ne_compte()
    {
        MecanismeCarte.Depuis([L(210, ClocksEventReason.GpuInactif), new HardwareReading(null, null)]).Should().BeNull();
        MecanismeCarte.Depuis([]).Should().BeNull();
    }

    [Fact]
    public void Jamais_bridee_ne_coute_rien_et_toujours_bridee_ne_se_lit_pas()
    {
        MecanismeCarte.Depuis([L(2730, ClocksEventReason.Aucune)])!.PerteEstimeePourcent.Should().Be(0);
        MecanismeCarte.Depuis([L(2700, ClocksEventReason.PlafondPuissanceLogiciel)])!.PerteEstimeePourcent.Should().BeNull();
    }

    [Fact]
    public void Les_captures_d_une_partie_se_cumulent()
    {
        var m = MecanismeCarte.Cumuler([new MecanismeCarte(20, 4, 2715, 2730), null, new MecanismeCarte(20, 0, null, 2740)])!;
        m.Echantillons.Should().Be(40);
        m.BridesPuissance.Should().Be(4);
        m.MhzLibre.Should().Be(2735);
    }

    // Un menu (puissance basse, non bridé) ne compte pas comme du temps « libre ».
    [Fact]
    public void Les_lectures_hors_charge_ne_comptent_pas()
    {
        var m = MecanismeCarte.Depuis([new HardwareReading(110, 60, 2715, ClocksEventReason.PlafondPuissanceLogiciel),
                                       new HardwareReading(105, 60, 2730, ClocksEventReason.Aucune),
                                       new HardwareReading(30, 50, 1500, ClocksEventReason.Aucune)])!;
        m.Echantillons.Should().Be(2);
        m.MhzLibre.Should().Be(2730);
    }
}
