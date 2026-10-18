using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>Réactiver le plan après PERFBOOSTMODE, ou changer de plan, peut remettre le mode
/// d'alimentation de Windows 11 à son défaut. Un powrprof factice qui le fait : rien n'est lu ni écrit sur la machine.</summary>
public sealed class ModeDAlimentationGardeTests
{
    private static readonly Guid Equilibre = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid MeilleuresPerformances = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private sealed class Powrprof : Win32PowerPlanActions.IAlimentation
    {
        public Guid? Plan = Equilibre;
        public Guid? Mode = MeilleuresPerformances;
        public bool ActiverRemetLeMode = true;
        public bool PoserEchoue;
        public int EchecsRestants = int.MaxValue;
        public bool PoserSansEffet;
        public int EcrituresReussies = int.MaxValue;
        public int ActivationsReussies = int.MaxValue;
        public readonly List<string> Appels = [];

        public Guid? PlanActif() => Plan;
        public uint Boost = 0;
        public uint EcrireValeurSecteur(Guid plan, Guid sousGroupe, Guid reglage, uint valeur)
        {
            Appels.Add("ecrire");
            if (EcrituresReussies-- <= 0) return 5;
            Boost = valeur;
            return 0;
        }
        public uint? LireValeurSecteur(Guid plan, Guid sousGroupe, Guid reglage) => Boost;
        public uint Activer(Guid plan)
        {
            Appels.Add("activer");
            if (ActivationsReussies-- <= 0) return 5;
            Plan = plan;
            if (ActiverRemetLeMode && Mode is not null) Mode = Guid.Empty;
            return 0;
        }
        public Guid? ModeSecteur() => Mode;
        public uint PoserModeSecteur(Guid mode)
        {
            Appels.Add("poser " + mode);
            if (PoserEchoue && EchecsRestants-- > 0) return 5;
            if (PoserSansEffet) return 0;
            Mode = mode;
            return 0;
        }
    }

    [Fact]
    public void Le_mode_remis_par_la_reactivation_du_plan_est_repose_et_relu()
    {
        var p = new Powrprof();
        new Win32PowerPlanActions(p).SetBoostModeAc(Equilibre, 2).IsOk.Should().BeTrue();
        p.Mode.Should().Be(MeilleuresPerformances);
        p.Appels.Should().Equal("ecrire", "activer", "poser " + MeilleuresPerformances);
    }

    [Fact]
    public void Le_changement_de_plan_garde_aussi_le_mode()
    {
        var p = new Powrprof();
        new Win32PowerPlanActions(p).SetActivePlan(Equilibre).IsOk.Should().BeTrue();
        p.Mode.Should().Be(MeilleuresPerformances);
    }

    [Fact]
    public void Un_mode_inchange_n_est_pas_reecrit_et_un_mode_non_lu_n_est_jamais_pose()
    {
        var p = new Powrprof { ActiverRemetLeMode = false };
        new Win32PowerPlanActions(p).SetBoostModeAc(Equilibre, 2).IsOk.Should().BeTrue();
        p.Appels.Should().Equal("ecrire", "activer");

        var avantWindows11 = new Powrprof { Mode = null };
        new Win32PowerPlanActions(avantWindows11).SetActivePlan(Equilibre).IsOk.Should().BeTrue();
        avantWindows11.Appels.Should().Equal("activer");
    }

    [Fact]
    public void Un_mode_qui_ne_se_repose_pas_est_un_echec_dit()
    {
        var p = new Powrprof { PoserEchoue = true };
        var r = new Win32PowerPlanActions(p).SetBoostModeAc(Equilibre, 2);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("power mode");

        var sansEffet = new Powrprof { PoserSansEffet = true };
        new Win32PowerPlanActions(sansEffet).SetActivePlan(Equilibre).IsOk.Should().BeFalse("relu : le mode n'est pas revenu");
    }
    // Le mode ne se repose pas, l'op est ratée : la valeur de boost d'avant revient (rien n'est laissé
    // changé sans retour), et un changement de plan raté rend le plan d'avant.
    [Fact]
    public void Une_op_ratee_ne_laisse_rien_change()
    {
        var p = new Powrprof { PoserEchoue = true };
        new Win32PowerPlanActions(p).SetBoostModeAc(Equilibre, 2).IsOk.Should().BeFalse();
        p.Boost.Should().Be(0u, "la valeur d'avant l'écriture est rendue");

        var q = new Powrprof { PoserEchoue = true, Plan = Equilibre };
        new Win32PowerPlanActions(q).SetActivePlan(MeilleuresPerformances).IsOk.Should().BeFalse();
        q.Plan.Should().Be(Equilibre, "le plan d'avant revient");
    }

    // Le mode d'avant aussi est retenté après le retour en arrière, et l'échec le dit.
    [Fact]
    public void Le_retour_en_arriere_retente_le_mode_et_dit_s_il_n_a_pas_pu()
    {
        var unEchec = new Powrprof { PoserEchoue = true, EchecsRestants = 1 };
        new Win32PowerPlanActions(unEchec).SetBoostModeAc(Equilibre, 2).IsOk.Should().BeFalse();
        unEchec.Mode.Should().Be(MeilleuresPerformances, "le mode d'avant est reposé au retour en arrière");

        var toujours = new Powrprof { PoserEchoue = true, Plan = Equilibre };
        (new Win32PowerPlanActions(toujours).SetActivePlan(MeilleuresPerformances).Error?.Message ?? "").Should().Contain("NOT set back");
    }

    // Le retour en arrière lui-même rate : la cible reste changée, l'échec le dit avec le code qui garde le journal, jamais
    // « set back ».
    [Fact]
    public void Un_retour_en_arriere_rate_se_dit_pose_et_garde_le_journal()
    {
        var valeur = new Powrprof { PoserEchoue = true, EcrituresReussies = 1 };
        var r = new Win32PowerPlanActions(valeur).SetBoostModeAc(Equilibre, 2);
        valeur.Boost.Should().Be(2u, "témoin : la réécriture de la valeur d'avant a raté");
        r.Error!.Value.Code.Should().Be(DrDxt.Core.ErrorCodes.TweakPartiallyApplied);
        r.Error.Value.Message.Should().NotContain("Boost mode set back");

        var plan = new Powrprof { PoserEchoue = true, Plan = Equilibre, ActivationsReussies = 1 };
        var p = new Win32PowerPlanActions(plan).SetActivePlan(MeilleuresPerformances);
        plan.Plan.Should().Be(MeilleuresPerformances, "témoin : le plan d'avant n'a pas pu revenir");
        p.Error!.Value.Code.Should().Be(DrDxt.Core.ErrorCodes.TweakPartiallyApplied);
    }
}
