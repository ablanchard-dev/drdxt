using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

// Curve Optimizer, guide BIOS pour un Ryzen de bureau 5000 et plus ; jamais écrit.
public sealed class CurveOptimizerAmdTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<ArretClasse> Stable = [];

    [Theory]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", true)]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", true)]
    [InlineData("AMD Ryzen 5 9600X 6-Core Processor", true)]
    [InlineData("AMD Ryzen 5 5600", true)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", false)]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", false)]
    [InlineData("AMD Ryzen 7 5800H with Radeon Graphics", false)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-14700F", false)]
    [InlineData(null, false)]
    public void Seul_un_ryzen_de_bureau_5000_et_plus_recoit_le_guide(string? cpu, bool guide)
        => (CurveOptimizerAmd.Juger(cpu, "ASUSTeK COMPUTER INC.", Stable, Maintenant) is not null).Should().Be(guide);

    [Fact]
    public void Le_chemin_n_est_donne_que_sur_une_carte_msi_et_la_source_et_le_risque_sont_dits()
    {
        var msi = CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "Micro-Star International Co., Ltd.", Stable, Maintenant)!;
        msi.Explication.Should().Contain("Precision Boost Overdrive").And.Contain("Click BIOS X").And.Contain("instabilité")
            .And.Contain("n'écrit jamais dans le BIOS").And.Contain("n'est pas mesuré");
        msi.Statut.Should().Be(RepairStatus.Observed);

        CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "Gigabyte Technology Co., Ltd.", Stable, Maintenant)!.Explication
            .Should().NotContain("Click BIOS X").And.Contain("manuel de la carte");
    }

    [Theory]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor")]
    [InlineData("AMD Ryzen 7 5700X3D 8-Core Processor")]
    [InlineData("AMD Ryzen 5 5600X3D 6-Core Processor")]
    public void Un_x3d_5000_n_est_pas_dit_accepter_le_reglage_que_amd_verrouille(string cpu)
    {
        var e = CurveOptimizerAmd.Juger(cpu, "ASUSTeK COMPUTER INC.", Stable, Maintenant)!.Explication;
        e.Should().NotContain("accepte Curve Optimizer").And.Contain("AMD verrouille").And.Contain("AGESA 1.2.0.8");
        CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "ASUSTeK COMPUTER INC.", Stable, Maintenant)!.Explication
            .Should().Contain("accepte Curve Optimizer");
    }

    // Une baisse de tension pousse la puce : jamais conseillée sur une machine qui s'arrête, ni quand on n'a pas pu le lire.
    [Theory]
    [InlineData(FamilleDArret.EcranBleu)]
    [InlineData(FamilleDArret.SansCode)]
    [InlineData(FamilleDArret.Veille)]
    public void Un_arret_recent_remplace_le_conseil_par_d_abord_stabiliser(FamilleDArret famille)
    {
        var c = CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "ASUSTeK COMPUTER INC.",
            [new ArretClasse(Maintenant.AddDays(-3), famille, famille == FamilleDArret.Veille ? 0 : 0x50)], Maintenant)!;
        c.Explication.Should().Contain("D'abord stabiliser").And.NotContain("accepte Curve Optimizer");
        c.Action.Should().NotBe(RepairAction.Recommandation);
    }

    [Fact]
    public void Un_journal_non_lu_ne_vaut_pas_stable_et_un_arret_ancien_ne_bloque_plus()
    {
        var nonLu = CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "ASUSTeK COMPUTER INC.", null, Maintenant)!;
        nonLu.Statut.Should().Be(RepairStatus.Unknown);
        nonLu.Action.Should().NotBe(RepairAction.Recommandation);

        CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "ASUSTeK COMPUTER INC.",
                [new ArretClasse(Maintenant - CandidateSelector.FenetreInstabilite - TimeSpan.FromDays(1), FamilleDArret.SansCode)], Maintenant)!
            .Action.Should().Be(RepairAction.Recommandation);
    }

    // Un écran bleu vu par son seul rapport ou son vidage, une erreur matérielle grave, une hausse des arrêts dangereux du
    // SSD : un seul incident daté suffit, même sans arrêt brutal.
    [Fact]
    public void Un_autre_incident_recent_suffit_a_retenir_le_conseil()
    {
        var c = CurveOptimizerAmd.Juger("AMD Ryzen 7 7800X3D 8-Core Processor", "ASUSTeK COMPUTER INC.", Stable, Maintenant,
            [Maintenant.AddDays(-2)])!;
        c.Action.Should().NotBe(RepairAction.Recommandation);
        c.Explication.Should().Contain("D'abord stabiliser");
    }
}
