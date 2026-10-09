using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// La fonction objectif — et la seule chose qu'elle doit absolument savoir faire.
///
/// **Le 1 % low doit pouvoir battre la moyenne.** Un joueur competitif prefere sans hesiter
/// +0,5 % de moyenne avec +7 % de 1 % low a +1,5 % de moyenne avec −3 % de 1 % low : le premier
/// profil est fluide, le second accroche. Une fonction objectif qui ne sait pas trancher ce
/// duel-la optimise la mauvaise chose, et tout l'autopilote avec elle.
/// </summary>
public class GameObjectiveTests
{
    private const OptimizationGoal Competitif = OptimizationGoal.Competitive;

    [Fact]
    public void LE_1_POURCENT_LOW_PEUT_BATTRE_LA_MOYENNE()
    {
        // L'exemple exact du brief, en partant de 100 / 60.
        var reference = new GameMetrics(AvgFps: 100, P1LowFps: 60);
        var candidatA = new GameMetrics(AvgFps: 101.5, P1LowFps: 58.2);   // moyenne +1,5 %, low −3 %
        var candidatB = new GameMetrics(AvgFps: 100.5, P1LowFps: 64.2);   // moyenne +0,5 %, low +7 %

        var baseScore = GameObjective.Score(reference, Competitif);
        var scoreA = GameObjective.Score(candidatA, Competitif);
        var scoreB = GameObjective.Score(candidatB, Competitif);

        scoreB.Should().BeGreaterThan(scoreA, "en mode fluidite, B doit gagner");
        scoreA.Should().BeLessThan(baseScore, "un 1 % low qui recule DEGRADE le profil competitif");
        scoreB.Should().BeGreaterThan(baseScore);
    }

    [Fact]
    public void Les_deux_poids_font_exactement_1()
    {
        // Sinon le score ne serait plus comparable a des FPS, et un ecart relatif n'aurait plus
        // le meme sens que partout ailleurs dans le produit.
        (GameObjective.PoidsMoyenne + GameObjective.PoidsUnPourCentLow).Should().Be(1.0);
    }

    [Fact]
    public void Le_1_pourcent_low_pese_PLUS_que_la_moyenne()
    {
        // C'est la seule chose qui distingue « Competitive » d'un « max FPS » deguise.
        GameObjective.PoidsUnPourCentLow.Should().BeGreaterThan(GameObjective.PoidsMoyenne);
    }

    [Fact]
    public void A_metriques_identiques_le_score_est_identique()
    {
        // Deterministe : deux passes identiques ne doivent jamais produire deux scores.
        var m = new GameMetrics(137.2, 88.1);

        GameObjective.Score(m, Competitif).Should().Be(GameObjective.Score(m, Competitif));
    }

    [Fact]
    public void Un_gain_sur_les_DEUX_grandeurs_gagne_toujours()
    {
        // Le controle qui empeche la ponderation de produire un resultat absurde.
        var avant = GameObjective.Score(new GameMetrics(100, 60), Competitif);
        var apres = GameObjective.Score(new GameMetrics(102, 63), Competitif);

        apres.Should().BeGreaterThan(avant);
    }

    [Fact]
    public void L_objectif_sait_s_EXPLIQUER_au_joueur()
    {
        // Un score qu'on ne peut pas expliquer ne se conteste pas, et l'UI devra le dire.
        GameObjective.Explication(Competitif)
            .Should().Contain("1 % low").And.Contain("65").And.Contain("35");
    }
}
