using DrDxt.Core;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les réglages processeur du plan actif se comparent au défaut de sa PERSONNALITÉ (PowerReadACDefaultIndex), pas du plan :
// un plan personnalisé a les deux différents. Et un plafond n'est un frein « à corriger » que s'il s'écarte de ce défaut.
public sealed class ReglagesDuPlanTests
{
    private static readonly Guid Equilibre = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid Perso = new("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Un_plan_personnalise_se_compare_au_defaut_de_sa_personnalite()
    {
        RepairAuditService.CleDeLecture(Perso, _ => Equilibre).Should().Be((Equilibre, Perso));
        RepairAuditService.CleDeLecture(Equilibre, g => g).Should().Be((Equilibre, Equilibre), "témoin : un plan Windows est sa propre personnalité");
    }

    [Fact]
    public void Un_plafond_egal_au_defaut_de_la_personnalite_n_est_pas_un_frein_a_corriger()
    {
        var auDefaut = new PowerSettingValue("maximum processeur", 99, 99, "%");
        var ecarte = new PowerSettingValue("maximum processeur", 99, 100, "%");
        var defautNonLu = new PowerSettingValue("maximum processeur", 99, null, "%");

        RepairAuditService.JugerReglageProcesseur(auDefaut, portable: false).Statut.Should().Be(RepairStatus.ExpectedDefault);
        RepairAuditService.JugerReglageProcesseur(ecarte, portable: false).Statut.Should().Be(RepairStatus.Suspicious, "témoin : un vrai frein");
        RepairAuditService.JugerReglageProcesseur(ecarte, portable: true).Statut.Should().Be(RepairStatus.NonDefault);
        RepairAuditService.JugerReglageProcesseur(defautNonLu, portable: false).Statut.Should().Be(RepairStatus.NonDefault,
            "défaut non lu : rien ne prouve un écart, le frein se dit sans être déclaré à corriger");
    }
}
