using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// 04/10/2026 (plan 2.5) : l'intégrité de la mémoire, dite dans les deux sens, jamais écrite.
public sealed class IntegriteMemoireTests
{
    [Fact]
    public void Coupee_elle_ne_coute_rien_et_la_contrepartie_est_dite()
    {
        var c = IntegriteMemoire.Juger(2, [0]).Should().ContainSingle().Subject;   // relevé de la machine de référence
        c.ValeurCourante.Should().Be("coupée");
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Contain("noyau").And.NotContain("liste de blocage", "revue : sur Windows 11 22H2+, la liste ne dépend plus d'elle");
    }

    [Fact]
    public void Active_son_cout_est_dit_avec_sa_seule_source_et_rien_n_est_coupe()
    {
        var c = IntegriteMemoire.Juger(2, [2]).Should().ContainSingle().Subject;
        c.ValeurCourante.Should().Be("active");
        c.Explication.Should().Contain("8 %").And.Contain("ne la coupe pas");
    }

    [Fact]
    // Revue adverse du docteur (09/10/2026, R7) : « rien n'est dit » faisait passer une famille non lue pour une saine.
    public void Non_lue_se_dit_non_lue()
    {
        IntegriteMemoire.Juger(null, [2]).Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown);
        IntegriteMemoire.Juger(2, null).Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown);
    }
}
