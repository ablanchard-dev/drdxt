using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

// Les analyses de Microsoft Defender contre les parties relevées, en lecture seule.
public sealed class AnalysesDefenderTests
{
    private static readonly DateTimeOffset J = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static PlageDeJeu Partie(int h, int duree = 60) => new(J.AddHours(h), J.AddHours(h).AddMinutes(duree));
    private static AnalyseDefender Analyse(double h, double minutes = 2, bool arretee = false)
        => new(J.AddHours(h), J.AddHours(h).AddMinutes(minutes), arretee);

    [Fact]
    public void Non_lu_n_est_jamais_aucune()
        => AnalysesDefender.Juger(null, [Partie(20)], true, 30).Statut.Should().Be(RepairStatus.Unknown);

    [Fact]
    public void Une_analyse_pendant_une_partie_se_dit()
    {
        var c = AnalysesDefender.Juger([Analyse(3), Analyse(20.5)], [Partie(20)], true, 30);
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurAttendue.Should().Be("1 pendant une partie");
        c.Explication.Should().Contain("2 analyses en 30 jours").And.Contain("ne touche pas à Defender");
    }

    [Fact]
    public void Aucune_pendant_les_parties_avec_le_reglage_par_defaut_est_attendu()
    {
        var c = AnalysesDefender.Juger([Analyse(3), Analyse(4, arretee: true)], [Partie(20), Partie(22)], true, 30);
        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
        c.Explication.Should().Contain("dont 1 arrêtée avant la fin").And.Contain("aucune pendant tes 2 parties");
    }

    [Fact]
    public void Analyser_meme_quand_le_pc_sert_se_dit_meme_sans_chevauchement()
        => AnalysesDefender.Juger([Analyse(3)], [Partie(20)], false, 30).Statut.Should().Be(RepairStatus.Suspicious);

    [Fact]
    public void Une_analyse_juste_avant_ou_juste_apres_la_partie_ne_compte_pas()
        => AnalysesDefender.Juger([Analyse(19, minutes: 59), Analyse(21.01)], [Partie(20)], true, 30).Statut
            .Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Sans_partie_relevee_rien_n_est_compare()
        => AnalysesDefender.Juger([Analyse(3)], [], true, 30).Statut.Should().Be(RepairStatus.Observed);

    // Une seule analyse pendant une partie : « dont 1 tombée », jamais « tombée(s) ».
    [Fact]
    public void Une_seule_analyse_pendant_une_partie_s_accorde_au_singulier()
    {
        var c = AnalysesDefender.Juger([Analyse(3), Analyse(20.5)], [Partie(20)], true, 30);
        c.Explication.Should().NotContain("(s)");
    }

    // Une analyse sans fin lue (encore en cours, ou coupée par un redémarrage) commencée avant la partie la couvre jusqu'au
    // début de l'analyse suivante, pas au-delà.
    [Fact]
    public void Une_analyse_sans_fin_commencee_avant_la_partie_compte()
    {
        var enCours = new AnalyseDefender(J.AddHours(21), null, false);
        AnalysesDefender.Juger([enCours], [Partie(22)], true, 30).ValeurAttendue.Should().Be("1 pendant une partie");
    }

    [Fact]
    public void Une_analyse_sans_fin_ne_couvre_pas_les_parties_apres_l_analyse_suivante()
    {
        var coupee = new AnalyseDefender(J.AddHours(1), null, false);
        AnalysesDefender.Juger([coupee, Analyse(3)], [Partie(20)], true, 30).Statut.Should().Be(RepairStatus.ExpectedDefault);
    }
}
