using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// L'horloge haute résolution mesurée : le coût affiché est celui qui est jugé, et rien n'est déduit de sa fréquence.
public sealed class HorlogeQpcTests
{
    [Fact]
    public void Une_lecture_rapide_est_le_compteur_du_processeur_et_rien_n_est_a_changer()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = HorlogeQpc.Juger(new(11.6, 0, 28));
        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
        c.ValeurCourante.Should().Be("11,6 ns par lecture");
        c.Explication.Should().Contain("11,6 ns");
    }

    // Notre règle : la valeur affichée est celle qui est jugée. 520 ns affichées sont jugées 520 ns.
    [Fact]
    public void Le_cout_affiche_est_celui_qui_est_juge()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = HorlogeQpc.Juger(new(520, 0, 28));
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Be("520,0 ns par lecture");
        c.Explication.Should().Contain("520 ns");
        HorlogeQpc.Juger(new(499, 0, 28)).Statut.Should().Be(RepairStatus.ExpectedDefault);
    }

    [Fact]
    public void Un_recul_entre_coeurs_est_dit_meme_si_la_lecture_est_rapide()
        => HorlogeQpc.Juger(new(11, 1, 28)).Statut.Should().Be(RepairStatus.Suspicious);

    [Fact]
    public void Rien_mesure_est_non_lu()
    {
        HorlogeQpc.Juger(null).Statut.Should().Be(RepairStatus.Unknown);
        HorlogeQpc.Juger(new(11, 0, 0)).Statut.Should().Be(RepairStatus.Unknown);
    }
}
