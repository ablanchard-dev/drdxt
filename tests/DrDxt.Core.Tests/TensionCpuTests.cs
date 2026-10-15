using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Le codage de la boîte aux lettres OC d'Intel
// (MSR 0x150) pour le décalage de tension. Valeurs attendues calculées depuis la source (erpalma/throttled,
// calc_undervolt_msr / calc_undervolt_mv), jamais par ce code : −50 mV sur les cœurs = 0x80000011F9A00000.
public sealed class TensionCpuTests
{
    [Fact]
    public void La_lecture_se_demande_par_la_commande_0x10_du_plan()
    {
        TensionCpu.CommandeLecture(TensionCpu.PlanCoeurs).Should().Be(0x8000001000000000UL);
        TensionCpu.CommandeLecture(TensionCpu.PlanCache).Should().Be(0x8000021000000000UL);
    }

    [Fact]
    public void Le_decalage_s_ecrit_comme_la_source_et_garde_les_bits_lus_du_BIOS()
    {
        TensionCpu.CommandeEcriture(TensionCpu.PlanCoeurs, -50, lecture: 0)!.Value.Should().Be(0x80000011F9A00000UL);
        TensionCpu.CommandeEcriture(TensionCpu.PlanCache, -50, lecture: 0)!.Value.Should().Be(0x80000211F9A00000UL);
        // Ratio, tension cible et mode posés par le BIOS (bits 20:0) : conservés, jamais remis à zéro.
        TensionCpu.CommandeEcriture(TensionCpu.PlanCoeurs, -50, lecture: 0x15CC00UL)!.Value.Should().Be(0x80000011F9B5CC00UL);
    }

    [Theory]
    [InlineData(0x00000000UL, 0)]
    [InlineData(0xF9A00000UL, -50)]
    [InlineData(0xF3400000UL, -100)]
    public void Le_decalage_lu_se_decode_en_millivolts(ulong reponse, int mv)
        => TensionCpu.DecalageMv(reponse).Should().Be(mv);

    [Fact]
    public void Une_reponse_en_erreur_ne_se_lit_pas()
        => TensionCpu.DecalageMv(0x0000000600000000UL).Should().BeNull("statut 6 : commande refusée, rien de lu");

    [Fact]
    public void Jamais_au_dessus_de_zero_ni_sous_le_plancher_ni_hors_des_plans_permis()
    {
        TensionCpu.CommandeEcriture(TensionCpu.PlanCoeurs, 10, 0).Should().BeNull("monter la tension : jamais");
        TensionCpu.CommandeEcriture(TensionCpu.PlanCoeurs, -TensionCpu.PlancherMv - 1, 0).Should().BeNull("sous le plancher : jamais");
        TensionCpu.CommandeEcriture(1, -50, 0).Should().BeNull("le plan de la carte graphique intégrée n'est pas touché");
        TensionCpu.CommandeEcriture(TensionCpu.PlanCoeurs, -50, 0x0000000600000000UL).Should().BeNull("lecture refusée : rien n'est écrit");
    }
}
