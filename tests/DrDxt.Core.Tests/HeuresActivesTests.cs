using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>
/// Les heures d'activité de Windows Update contre les heures des parties relevées. Les valeurs sont
/// celles relevées sur la machine de référence le 06/10/2026 : heures 15 h à 8 h, une partie de 8:23 à 8:44,
/// un redémarrage de Windows Update à 10:15.
/// </summary>
public sealed class HeuresActivesTests
{
    private static readonly TimeSpan Ete = TimeSpan.FromHours(2);
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.CreateCustomTimeZone("paris-ete", Ete, "paris-ete", "paris-ete");
    private static readonly HeuresActivesLues Reference = new(15, 8, null);

    private static PlageDeJeu Partie(int jour, int h1, int m1, int h2, int m2) =>
        new(new DateTimeOffset(2026, 10, jour, h1, m1, 0, Ete), new DateTimeOffset(2026, 10, jour, h2, m2, 0, Ete));

    private static readonly PlageDeJeu Du0410 = Partie(4, 8, 23, 8, 44);
    private static readonly DateTimeOffset Redemarrage0610 = new(2026, 10, 6, 10, 15, 3, Ete);

    [Fact]
    public void Une_plage_qui_passe_minuit_couvre_ses_heures_et_exclut_sa_fin()
    {
        HeuresActives.Couvre(15, 8, 23).Should().BeTrue();
        HeuresActives.Couvre(15, 8, 7).Should().BeTrue();
        HeuresActives.Couvre(15, 8, 8).Should().BeFalse("la fin est exclue : 8 h signifie jusqu'à 8:00");
        HeuresActives.Couvre(15, 8, 14).Should().BeFalse();
        HeuresActives.Duree(15, 8).Should().Be(17);
    }

    [Fact]
    public void La_partie_du_04_10_tombe_hors_des_heures_de_la_machine_de_reference()
        => HeuresActives.Hors(Reference, [Du0410], Paris).Should().ContainSingle();

    [Fact]
    public void Proposer_garde_les_heures_actuelles_et_ajoute_celle_de_la_partie_dans_la_limite_de_18_h()
    {
        HeuresActives.Proposer(Reference, [Du0410], Paris).Should().Be((15, 9));
        HeuresActives.Duree(15, 9).Should().Be(HeuresActives.DureeMax);
    }

    [Fact]
    public void Rien_a_proposer_sans_partie_ou_quand_elles_sont_deja_couvertes()
    {
        HeuresActives.Proposer(Reference, [], Paris).Should().BeNull();
        HeuresActives.Proposer(Reference, [Partie(4, 21, 0, 23, 30)], Paris).Should().BeNull();
    }

    [Fact]
    public void Quand_l_union_depasse_18_h_les_parties_passent_avant_les_heures_actuelles()
    {
        // 8 h à 17 h (le défaut de Windows) + des parties de 19 h à 3 h : l'union (8 h à 3 h, 19 h) dépasse 18 h.
        var p = HeuresActives.Proposer(new HeuresActivesLues(8, 17, false), [Partie(4, 19, 10, 23, 50), Partie(5, 0, 5, 2, 40)], Paris);
        p.Should().Be((9, 3), "toutes les heures jouées, et 8 des 9 heures actuelles : seule 8 h tombe");
    }

    [Fact]
    public void Des_parties_etalees_sur_plus_de_18_h_ne_se_couvrent_pas()
        => HeuresActives.Proposer(Reference, [Partie(4, 0, 10, 0, 30), Partie(4, 6, 10, 6, 30), Partie(4, 12, 10, 12, 30),
                Partie(4, 18, 10, 18, 30)], Paris)
            .Should().BeNull("0 h, 6 h, 12 h et 18 h demandent 19 h au moins");

    [Fact]
    public void Le_constat_reel_dit_la_partie_hors_des_heures_et_le_redemarrage_hors_partie()
    {
        var c = HeuresActives.Juger(Reference, [Du0410], [Redemarrage0610], 30, Paris);

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.ValeurCourante.Should().Be("15 h à 8 h");
        // Le mode n'est pas lu sur cette machine ; DrDXT ne propose pas d'écrire, il dit
        // comment passer en manuel et quelle plage couvrirait la partie. En manuel lu, la plage devient la valeur attendue.
        c.ValeurAttendue.Should().BeNull();
        c.Explication.Should().Contain("Windows ne dit pas s'il les règle tout seul").And.Contain("de 15 h à 9 h couvrirait tes parties");
        HeuresActives.Juger(Reference with { Automatique = false }, [Du0410], [Redemarrage0610], 30, Paris)
            .ValeurAttendue.Should().Be("15 h à 9 h");
        c.Explication.Should().Contain("1 partie relevée sur 1 tombe hors de tes heures d'activité (15 h à 8 h)")
            .And.Contain("1 fois en 30 jours (dernière fois le 06/10 à 10:15), dont 0 pendant une partie relevée")
            .And.Contain("Windows Update reste allumé");
    }

    [Fact]
    public void Un_redemarrage_pendant_une_partie_est_compte()
        => HeuresActives.Juger(Reference, [Du0410], [new DateTimeOffset(2026, 10, 4, 8, 30, 0, Ete)], 30, Paris)
            .Explication.Should().Contain("dont 1 pendant une partie relevée");

    [Fact]
    public void Non_lu_n_est_jamais_bien_regle_ni_aucun_redemarrage()
    {
        var c = HeuresActives.Juger(null, [Du0410], null, 30, Paris);
        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("n'ont pas pu être lues").And.Contain("n'ont pas pu être lus");
    }

    [Fact]
    public void En_mode_automatique_DrDXT_ne_propose_pas_de_plage_et_renvoie_a_Parametres()
    {
        var c = HeuresActives.Juger(Reference with { Automatique = true }, [Du0410], [], 30, Paris);
        c.ValeurAttendue.Should().BeNull();
        c.Explication.Should().Contain("Windows les règle tout seul").And.NotContain("« Optimiser » propose");
    }

    [Fact]
    public void Parties_couvertes_se_disent_couvertes()
        => HeuresActives.Juger(Reference, [Partie(4, 21, 0, 23, 30)], [], 30, Paris).Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Optimiser_le_pose_au_clic_sans_jeu_sans_mesure_et_malgre_des_arrets_brutaux()
    {
        var arrets = Enumerable.Range(1, 3).Select(j => new ArretClasse(DateTimeOffset.UtcNow.AddDays(-j), FamilleDArret.SansCode)).ToList();
        var j = JugementApply.Juger(DiagnosticDeScan.Construire(null, null, null, arrets), ["fixit.heures_actives"], jeuEnCours: false);

        j.PosesSansMesure.Should().Equal("fixit.heures_actives");
        j.LotAChaud.Should().BeEmpty("aucun A/B en jeu : il ne change pas les images");
        j.AuRedemarrage.Should().BeEmpty();
        j.RienATenter.Should().BeFalse();
        j.Appreciations.Single().Motifs.Should().ContainSingle(m => m.Contains("sans effet sur les images"));
    }

    [Fact]
    public void Une_lecture_hors_de_0_a_23_ou_absente_est_non_lue()
    {
        HeuresActives.Depuis(15, 8, null).Should().Be(new HeuresActivesLues(15, 8, null));
        HeuresActives.Depuis(15, 8, 1)!.Automatique.Should().BeTrue();
        HeuresActives.Depuis(24, 8, null).Should().BeNull();
        HeuresActives.Depuis(null, 8, null).Should().BeNull();
    }
    // La plage proposée peut retirer des heures protégées aujourd'hui ; le constat les nomme.
    [Fact]
    public void Les_heures_que_la_plage_proposee_ne_protege_plus_sont_dites()
    {
        HeuresActives.Perdues(new(8, 17, false), (9, 3)).Should().Contain("de 8 h à 9 h");
        HeuresActives.Perdues(new(15, 8, false), (15, 9)).Should().BeEmpty("la plage proposée garde toutes les heures actuelles");
    }
}
