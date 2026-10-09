using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// 06/10/2026 (chantier B4) : le plan « Économie d'énergie » et le mode d'alimentation de Windows 11. Source :
// Microsoft Learn, « Customize the Windows performance power slider » : le mode ne s'applique qu'avec le plan Équilibré, et
// sur un PC sans batterie aucun réglage n'y est attaché par défaut.
public sealed class ModeAlimentationTests
{
    private const string Equilibre = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string Economie = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string Hautes = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string MeilleureAutonomie = "961cc777-2547-4f9d-8174-7d86181b8a7a";
    private const string MeilleuresPerformances = "ded574b5-45a0-4f42-8737-46345c09c238";

    [Fact]
    public void Le_plan_Economie_est_dit_avec_un_geste_et_sans_chiffre()
    {
        var c = ModeAlimentation.JugerPlan(Economie);

        c.ValeurCourante.Should().Be("Économie d'énergie");
        c.Statut.Should().Be(RepairStatus.NonDefault);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.Explication.Should().Contain("Équilibré").And.NotContain("%");
    }

    [Fact]
    public void Equilibre_est_le_defaut_et_un_autre_plan_reste_un_choix_probable()
    {
        ModeAlimentation.JugerPlan(Equilibre).Statut.Should().Be(RepairStatus.ExpectedDefault);
        ModeAlimentation.JugerPlan(Hautes).Statut.Should().Be(RepairStatus.UserChoiceProbable);
        ModeAlimentation.JugerPlan(null).Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void Portable_branche_en_meilleure_autonomie_c_est_dit()
    {
        var c = ModeAlimentation.JugerMode(Equilibre, MeilleureAutonomie, batterie: true).Single();

        c.Nom.Should().Be("mode d'alimentation (secteur)");
        c.ValeurCourante.Should().Be("Meilleure autonomie");
        c.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void Sans_batterie_le_mode_n_a_pas_de_reglage_par_defaut_et_c_est_dit_sans_geste()
    {
        var c = ModeAlimentation.JugerMode(Equilibre, MeilleureAutonomie, batterie: false).Single();

        c.Statut.Should().Be(RepairStatus.Observed);
        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().Contain("sans batterie");
    }

    [Fact]
    public void Hors_du_plan_Equilibre_le_mode_ne_s_applique_pas_et_rien_n_est_dit()
        => ModeAlimentation.JugerMode(Hautes, MeilleureAutonomie, batterie: true).Should().BeEmpty();

    // Revue F-12 : un plan de constructeur dérivé d'Équilibré a la personnalité Équilibré ; c'est elle qu'on passe.
    [Fact]
    public void Un_plan_derive_d_Equilibre_est_juge_comme_Equilibre()
        => ModeAlimentation.JugerMode(Equilibre, MeilleureAutonomie, batterie: true).Should().ContainSingle();

    // Revue F-13 : batterie non lue, rien n'est dit (et rien ne casse).
    [Fact]
    public void Batterie_non_lue_rien_n_est_dit()
        => ModeAlimentation.JugerMode(Equilibre, MeilleureAutonomie, batterie: null).Should().BeEmpty();

    [Fact]
    public void Performances_ou_mode_non_lu_rien_n_est_dit()
    {
        ModeAlimentation.JugerMode(Equilibre, MeilleuresPerformances, batterie: true).Should().BeEmpty();
        ModeAlimentation.JugerMode(Equilibre, null, batterie: true).Should().BeEmpty();
    }
}
