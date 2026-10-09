using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// `AbVerdict.IsMeasurableGain` est un booleen, et c'est lui qui force a mentir.
///
/// Le banc deterministe resout **0,1 %** (mesure du 06/09 : sigma/moyenne 0,122 % sur 10 passes).
/// Le rejeu in-game resout **~13 %** (sigma/base 6,46 %, mesure du 06/09 sur 28 paires). Un seul
/// booleen ne peut pas porter cette difference : il oblige soit a **jeter** un gain sous-systeme
/// reel parce que le jeu ne le resout pas, soit a le **survendre** comme un gain FPS.
///
/// Le cas concret qui a impose ce modele (mesure du 07/09 sur la RTX 4060) : l'offset `+100/100`
/// donne **+2,7 % de calcul GPU a watts identiques**, reproductible, controle A/A' a 0,72 %. Et il
/// est **3,6 fois sous le plancher in-game de 9,9 %**. Les deux affirmations sont vraies en meme
/// temps. Le produit doit pouvoir dire les deux.
///
/// D'ou trois etats, et une RESOLUTION portee a cote de chaque delta :
///   ProvenGain / ProvenRegression — l'ecart depasse ce que l'experience sait resoudre ;
///   NoResolvedChange              — le signal est sous le bruit de cette experience. Ce n'est
///                                   Pas « pas de gain » : c'est « pas resolu ici » ;
///   Invalid                       — l'experience elle-meme ne tient pas (perte de focus, capture
///                                   defaillante). Aucun chiffre n'en sort.
///
/// `NoResolvedChange` et `Invalid` sont deux choses opposees, et les confondre est le defaut que
/// ce modele existe pour empecher : la premiere a mesure et n'a rien vu, la seconde n'a pas mesure.
/// </summary>
public class PerformanceEvidenceTests
{
    [Fact]
    public void Un_ecart_au_dessus_de_la_resolution_est_un_GAIN_PROUVE()
    {
        // Le banc GPU : +2,7 % pour une resolution de +/-0,3 %.
        Evidence.From(deltaPct: 2.7, resolutionPct: 0.3).Outcome
            .Should().Be(EvidenceOutcome.ProvenGain);
    }

    [Fact]
    public void Un_ecart_NEGATIF_au_dessus_de_la_resolution_est_une_REGRESSION_PROUVEE()
    {
        // Il faut pouvoir dire « ca a empire », sinon le produit ne sait garder que ce qui aide
        // par hasard. C'est la moitie manquante d'une boucle qui « conserve ce qui ameliore ».
        Evidence.From(-2.7, 0.3).Outcome.Should().Be(EvidenceOutcome.ProvenRegression);
    }

    [Fact]
    public void Sous_la_resolution_ce_n_est_PAS_une_absence_de_gain_mais_une_absence_de_RESOLUTION()
    {
        // Le meme +2,7 %, vu en jeu, avec la resolution reelle d'une session de rejeu.
        var preuve = Evidence.From(2.7, 4.8);

        preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange);
        // Le delta est CONSERVE : il faudra bien l'afficher pour dire « non resolu a +/-4,8 % ».
        preuve.DeltaPct.Should().Be(2.7);
        preuve.ResolutionPct.Should().Be(4.8);
    }

    [Fact]
    public void Exactement_a_la_resolution_ne_prouve_rien()
    {
        // La borne se resout du cote prudent. Un instrument qui prouve pile a son propre seuil
        // prouve son seuil, pas un gain.
        Evidence.From(4.8, 4.8).Outcome.Should().Be(EvidenceOutcome.NoResolvedChange);
    }

    [Fact]
    public void Une_resolution_INCONNUE_ne_permet_aucune_conclusion()
    {
        // `InterRunMarginOfError` vaut +∞ sous 3 runs, et c'est exactement ce cas : on a un ecart,
        // on ne sait pas ce qu'il vaut. Affirmer serait inventer une resolution.
        Evidence.From(50.0, double.PositiveInfinity).Outcome
            .Should().Be(EvidenceOutcome.NoResolvedChange);
    }

    [Fact]
    public void Une_experience_INVALIDE_ne_rend_aucun_chiffre()
    {
        // Perte de focus, capture defaillante, charge concurrente : l'experience ne tient pas.
        var preuve = Evidence.Invalide("perte de focus pendant la passe APRES");

        preuve.Outcome.Should().Be(EvidenceOutcome.Invalid);
        preuve.InvalidReason.Should().Contain("focus");
        preuve.EstConcluante.Should().BeFalse();
    }

    [Fact]
    public void INVALIDE_et_NON_RESOLU_ne_se_confondent_jamais()
    {
        // Le defaut que ce modele existe pour empecher. « Je n'ai rien vu » et « je n'ai pas
        // regarde » se ressemblent dans un booleen et n'ont rien a voir pour le client.
        Evidence.From(1.0, 4.8).Outcome.Should().NotBe(EvidenceOutcome.Invalid);
        Evidence.Invalide("capture vide").Outcome.Should().NotBe(EvidenceOutcome.NoResolvedChange);
    }

    [Fact]
    public void Le_sous_systeme_et_le_jeu_coexistent_au_lieu_de_s_exclure()
    {
        // La conclusion cible, litteralement : « debit GPU +2,7 % prouve a +/-0,3 % ; impact FPS
        // +1,2 %, non resolu avec une resolution de +/-4,8 % ». Deux affirmations vraies ensemble.
        var rapport = new PerformanceEvidence(
            new SubsystemEvidence("gpu.debit", Evidence.From(2.7, 0.3)),
            new GameEvidence("fps.moyen", Evidence.From(1.2, 4.8)));

        rapport.Subsystem!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenGain);
        rapport.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange);

        // Et le produit doit savoir qu'il a quelque chose a dire, sans que ce soit un gain FPS.
        rapport.QuelqueChoseDeProuve.Should().BeTrue();
        rapport.GainEnJeuProuve.Should().BeFalse();
    }

    [Fact]
    public void Un_rapport_sans_aucune_mesure_n_affirme_rien()
    {
        var vide = new PerformanceEvidence(null, null);

        vide.QuelqueChoseDeProuve.Should().BeFalse();
        vide.GainEnJeuProuve.Should().BeFalse();
    }

    [Fact]
    public void Une_regression_en_jeu_n_est_JAMAIS_comptee_comme_un_gain_prouve()
    {
        // La garde qui empeche une boucle d'optimisation de « garder » ce qui a empire.
        var rapport = new PerformanceEvidence(
            new SubsystemEvidence("gpu.debit", Evidence.From(2.7, 0.3)),
            new GameEvidence("fps.moyen", Evidence.From(-11.0, 4.8)));

        rapport.GainEnJeuProuve.Should().BeFalse();
        rapport.RegressionEnJeuProuvee.Should().BeTrue();
    }
}
