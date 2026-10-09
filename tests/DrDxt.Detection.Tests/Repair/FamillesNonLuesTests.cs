using DrDxt.Application.Parties;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Revue adverse du docteur (09/10/2026, R7) : trois familles se taisaient quand leur lecture échouait. Sans droits
/// administrateur, bcdedit refuse, et l'écran de récupération coupé disparaissait ; une lecture WMI ratée faisait disparaître
/// l'intégrité de la mémoire ; « après un changement » se taisait si un journal était illisible. Une famille muette est
/// indistinguable d'une famille saine : chacune dit maintenant « non lu ».
/// </summary>
public class FamillesNonLuesTests
{
    [Fact]
    public void Une_configuration_de_demarrage_illisible_se_dit_non_lue_avec_sa_cause()
    {
        var c = RepairAuditService.JugerBcd(null, eleve: false).Single();
        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("administrateur");

        RepairAuditService.JugerBcd("bootstatuspolicy        IgnoreAllFailures", eleve: true)
            .Should().ContainSingle(x => x.Nom == "bootstatuspolicy" && x.Statut == RepairStatus.Suspicious);
    }

    [Fact]
    public void L_integrite_de_la_memoire_non_lue_se_dit_non_lue()
        => IntegriteMemoire.Juger(null, null).Should().ContainSingle()
            .Which.Statut.Should().Be(RepairStatus.Unknown);

    [Fact]
    public void Apres_un_changement_recent_des_journaux_illisibles_se_disent()
    {
        var maintenant = DateTimeOffset.Now;
        StabiliteApresChangement.NonLu([new Changement(maintenant.AddDays(-3), "un réglage")], maintenant)
            .Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown);
        StabiliteApresChangement.NonLu([new Changement(maintenant.AddDays(-60), "un vieux réglage")], maintenant)
            .Should().BeEmpty("sans changement récent, il n'y a rien à rapprocher");
    }
}
