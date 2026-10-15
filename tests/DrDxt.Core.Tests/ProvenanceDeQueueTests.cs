using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Une question ouverte, et l'instrument qui servira a la trancher.
///
/// `DecideParMetrique` garde un reglage sur une grandeur de queue prouvee : le 1 % low **ou** le
/// p95 frametime. Deux mesures se contredisent sur ce que « p95 seul » vaut vraiment (voir
/// <see cref="PreuveDeQueue"/> : dans une mesure les deux grandeurs se separent, dans l'autre le p95
/// partage 95,5 % de sa variance avec la moyenne). Un cas de chaque cote ne tranche rien.
///
/// D'ou un ENREGISTREMENT, pas un durcissement : chaque verdict nomme ce qui a valide sa
/// queue. Au bout de vraies sessions, la question se tranchera sur un corpus au lieu de deux
/// mesures isolees.
///
/// Ce fichier existe autant pour prouver que rien n'a bouge. La moitie de ses tests verifie
/// que la decision (garder ou rejeter, et pourquoi) ne depend pas de cet enregistrement.
/// </summary>
public class ProvenanceDeQueueTests
{
    private static MetricEvidence M(GameMetricKind k, double delta, double resolution) =>
        new(k, Evidence.From(delta, resolution));

    // ± 1 % de resolution : un ecart de 5 % prouve, un ecart de 0,2 % ne prouve rien.
    private const double Resolution = 1.0;

    private static KeepDecision Decider(params MetricEvidence[] preuves) =>
        KeepPolicy.DecideParMetrique(preuves, OptimizationGoal.Competitive);

    // ── Fidelite de l'enregistrement ────────────────────────────────────────────────────────

    /// <summary>Le cas a compter : la moyenne monte, le p95 s'ameliore, le 1 % low ne tranche
    /// pas. Le reglage est garde — comme avant — et le verdict dit desormais sur QUOI.</summary>
    [Fact]
    public void Un_keep_obtenu_sur_le_p95_SEUL_se_nomme_P95Seul()
    {
        var d = Decider(
            M(GameMetricKind.Moyenne, 5.0, Resolution),
            M(GameMetricKind.P95Frametime, 4.0, Resolution),
            M(GameMetricKind.UnPourCentLow, 0.2, Resolution));

        d.Keep.Should().BeTrue("c'est la decision d'avant, et elle ne doit pas bouger");
        d.Queue.Should().Be(PreuveDeQueue.P95Seul);
    }

    [Fact]
    public void Un_keep_obtenu_sur_le_1_pour_cent_low_SEUL_se_nomme_UnPourCentLowSeul()
    {
        var d = Decider(
            M(GameMetricKind.Moyenne, 5.0, Resolution),
            M(GameMetricKind.P95Frametime, 0.2, Resolution),
            M(GameMetricKind.UnPourCentLow, 4.0, Resolution));

        d.Keep.Should().BeTrue();
        d.Queue.Should().Be(PreuveDeQueue.UnPourCentLowSeul);
    }

    [Fact]
    public void Un_keep_ou_LES_DEUX_grandeurs_de_queue_sont_prouvees_se_nomme_LesDeux()
    {
        var d = Decider(
            M(GameMetricKind.Moyenne, 5.0, Resolution),
            M(GameMetricKind.P95Frametime, 4.0, Resolution),
            M(GameMetricKind.UnPourCentLow, 6.0, Resolution));

        d.Keep.Should().BeTrue();
        d.Queue.Should().Be(PreuveDeQueue.LesDeux);
    }

    /// <summary>Une etiquette n'a de valeur que si elle sait aussi se taire. Un verdict qui ne
    /// garde pas n'a valide aucune queue, et le dire faussement polluerait le corpus exactement
    /// la ou il doit etre propre.</summary>
    [Fact]
    public void Un_verdict_qui_NE_GARDE_PAS_ne_nomme_aucune_grandeur_de_queue()
    {
        var rienNeTranche = Decider(
            M(GameMetricKind.Moyenne, 0.2, Resolution),
            M(GameMetricKind.P95Frametime, 0.1, Resolution),
            M(GameMetricKind.UnPourCentLow, 0.1, Resolution));

        rienNeTranche.Keep.Should().BeFalse();
        rienNeTranche.Queue.Should().Be(PreuveDeQueue.Aucune);

        // La moyenne monte seule : la fluidite reste inconnue, et c'est un revert depuis toujours.
        var moyenneSeule = Decider(
            M(GameMetricKind.Moyenne, 5.0, Resolution),
            M(GameMetricKind.P95Frametime, 0.2, Resolution),
            M(GameMetricKind.UnPourCentLow, 0.2, Resolution));

        moyenneSeule.Keep.Should().BeFalse();
        moyenneSeule.RaisonCle.Should().Be(KeepPolicy.CleFluiditeNonResolue);
        moyenneSeule.Queue.Should().Be(PreuveDeQueue.Aucune);
    }

    // ── Non-regression : la decision n'a pas bouge ──────────────────────────────────────────

    /// <summary>
    /// Le cas mesure le 08/09/2026, celui qui justifie de ne pas durcir la regle : un reglage
    /// qui ajoute un a-coup toutes les 60 frames rend **+1,6 %** de p95 pendant que le 1 % low
    /// s'effondre de **−65,6 %**.
    ///
    /// Exiger le 1 % low pour garder n'aurait rien change ici : la REGRESSION prouvee interdit
    /// deja le keep, et elle passe avant la queue. Ce test verrouille ce fait — l'enregistrement
    /// n'a pas rendu ce verdict plus permissif d'un cheveu.
    /// </summary>
    [Fact]
    public void Le_cas_du_08_09_un_p95_qui_monte_pendant_que_le_1_pour_cent_low_s_effondre()
    {
        var d = Decider(
            M(GameMetricKind.Moyenne, 1.0, Resolution),
            M(GameMetricKind.P95Frametime, 1.6, Resolution),
            M(GameMetricKind.UnPourCentLow, -65.6, Resolution));

        d.Keep.Should().BeFalse("une regression prouvee interdit le keep, quoi que rende le p95");
        d.RaisonCle.Should().Be(KeepPolicy.CleRegression);
        d.Queue.Should().Be(PreuveDeQueue.Aucune,
            "un p95 prouve n'a rien VALIDE ici : le verdict est un revert");
    }

    /// <summary>L'autre bout du meme fait : le p95 seul garde, et il gardait deja. Si ce test
    /// tombe un jour, c'est que quelqu'un a durci la regle — ce qui est une decision produit, pas
    /// un effet de bord d'un champ d'enregistrement.</summary>
    [Fact]
    public void Le_p95_SEUL_garde_toujours_autant_qu_avant()
    {
        Decider(
            M(GameMetricKind.Moyenne, 0.1, Resolution),
            M(GameMetricKind.P95Frametime, 4.0, Resolution),
            M(GameMetricKind.UnPourCentLow, 0.1, Resolution))
            .Keep.Should().BeTrue(
                "le critere de decision est INCHANGE : une seule grandeur de queue prouvee suffit");
    }

    /// <summary>La lecture partagee, appliquee a ce qu'un recolteur de corpus a sous la main : la
    /// liste de preuves d'un `CandidateResult`. Une seconde definition de « ce qui compte comme
    /// queue », ecrite cote recolteur, derivereait en silence.</summary>
    [Fact]
    public void La_meme_lecture_sert_a_la_politique_et_a_qui_recolte_le_corpus()
    {
        var preuves = new[]
        {
            M(GameMetricKind.Moyenne, 5.0, Resolution),
            M(GameMetricKind.P95Frametime, 4.0, Resolution),
            M(GameMetricKind.UnPourCentLow, 0.2, Resolution),
        };

        KeepPolicy.QueueQuiAValide(preuves).Should().Be(Decider(preuves).Queue);

        // Sans mesure du tout, on ne nomme rien — et surtout pas « les deux ».
        KeepPolicy.QueueQuiAValide(null).Should().Be(PreuveDeQueue.Aucune);
        KeepPolicy.QueueQuiAValide(Array.Empty<MetricEvidence>()).Should().Be(PreuveDeQueue.Aucune);
    }
}
