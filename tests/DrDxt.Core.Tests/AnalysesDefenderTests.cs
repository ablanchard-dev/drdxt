using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

// 07/10/2026 (chantier D3) : les analyses de Microsoft Defender contre les parties relevées, en lecture seule.
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
}
