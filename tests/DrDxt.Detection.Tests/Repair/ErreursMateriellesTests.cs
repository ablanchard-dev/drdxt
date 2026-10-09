using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// 04/10/2026 (plan 3.2) : les erreurs matérielles WHEA, jugées sur la gravité de l'en-tête CPER.
public sealed class ErreursMateriellesTests
{
    // Début de l'enregistrement relevé sur la machine de référence le 17/09 : « CPER », révision 0101, FFFFFFFF, 1 section,
    // gravité 3 (information).
    private static readonly byte[] Releve = Convert.FromHexString("435045520101FFFFFFFF0100030000000100");

    [Fact]
    public void La_gravite_se_lit_dans_l_en_tete_CPER()
    {
        ErreursMaterielles.Gravite(Releve).Should().Be(3);
        ErreursMaterielles.Gravite([1, 2, 3]).Should().BeNull();
        ErreursMaterielles.Gravite(Convert.FromHexString("585858580101FFFFFFFF0100020000000000")).Should().BeNull("pas de signature CPER");
    }

    [Fact]
    public void Une_information_n_est_pas_une_erreur()
    {
        var c = ErreursMaterielles.Juger([new(DateTimeOffset.Now.AddDays(-17), 3)]).Should().ContainSingle().Subject;
        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
        c.Explication.Should().Contain("aucune erreur").And.Contain("1 enregistrement");
    }

    // Revue du 04/10 (P1) : un journal illisible n'est pas « aucune erreur ».
    [Fact]
    public void Un_journal_non_lu_se_dit_non_lu()
        => ErreursMaterielles.Juger(null).Single().Statut.Should().Be(RepairStatus.Unknown);

    [Fact]
    public void Une_erreur_corrigee_se_dit_et_une_grave_pese_plus()
    {
        var t = DateTimeOffset.Now;
        ErreursMaterielles.Juger([new(t, 2)]).Single().ValeurCourante.Should().Be("1 corrigée(s)");
        var grave = ErreursMaterielles.Juger([new(t, 2), new(t.AddHours(-1), 1)]).Single();
        grave.Statut.Should().Be(RepairStatus.Suspicious);
        grave.ValeurCourante.Should().Be("1 grave(s), 1 corrigée(s)");
    }
}
