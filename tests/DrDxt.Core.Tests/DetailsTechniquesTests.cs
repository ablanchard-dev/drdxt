using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// « Détails techniques » : l'information d'expert sans problème ni geste se replie ; un problème ou un geste reste en haut.
public sealed class DetailsTechniquesTests
{
    private static RepairFinding C(string domaine, string nom, RepairStatus statut, RepairAction action = RepairAction.Aucune)
        => new(domaine, nom, "lu", null, statut, "parce que.", action);

    [Fact]
    public void Une_famille_d_expert_sans_probleme_va_dans_les_details()
    {
        DetailsTechniques.EnFait(C("CPU", "etats de veille", RepairStatus.Observed)).Should().BeTrue();
        DetailsTechniques.EnFait(C(TracesDOptimiseurs.Domaine, "traces : MMCSS (SystemResponsiveness)", RepairStatus.Observed))
            .Should().BeTrue("tout le domaine des traces datées");
    }

    [Fact]
    public void Un_probleme_ou_un_geste_reste_en_haut()
    {
        DetailsTechniques.EnFait(C("CPU", "etats de veille", RepairStatus.Suspicious)).Should().BeFalse();
        DetailsTechniques.EnFait(C("CPU", "etats de veille", RepairStatus.KnownHarmful)).Should().BeFalse();
        DetailsTechniques.EnFait(C("CPU", "caches L3", RepairStatus.Observed, RepairAction.Recommandation)).Should().BeFalse();
        DetailsTechniques.EnFait(C("CPU", "caches L3", RepairStatus.Unknown)).Should().BeFalse("un non-lu reste dans « Pas pu lire »");
    }

    [Fact]
    public void Un_constat_hors_des_familles_listees_ne_bouge_pas()
        => DetailsTechniques.EnFait(C("CPU", "frequence effective", RepairStatus.Observed)).Should().BeFalse();

    [Fact]
    public void Le_radar_ne_se_replie_que_quand_sa_table_est_vide()
    {
        var vide = RadarDesRegressions.Juger(new TablesDeDonnees(1, null, []), [], [], "CPU").Single();
        DetailsTechniques.EnFait(vide).Should().BeTrue();
        DetailsTechniques.EnFait(C(RadarDesRegressions.Domaine, RadarDesRegressions.Nom, RepairStatus.Observed)).Should().BeFalse();
    }
}
