using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// La regle qui protege la promesse — un gain de sous-systeme n'est pas un gain de jeu.
///
/// Le premier autopilote a obtenu **+2,54 % de debit GPU prouve a ±1,53 %** sur le banc
/// deterministe. Vrai resultat, et pour un profil GAMING il ne suffit pas : le banc mesure ce que
/// la carte calcule, le joueur ressent ce que le jeu affiche, et entre les deux il y a le CPU, le
/// moteur, le pilote et la scene.
///
/// Sans cette regle, DrDXT garderait des reglages sur la foi d'un chiffre synthetique et
/// promettrait des FPS qu'il n'a jamais mesures. C'est exactement le produit qu'on refuse d'etre.
/// </summary>
public class KeepPolicyTests
{
    private static PerformanceEvidence Preuve(
        (double delta, double resolution)? sousSysteme = null,
        (double delta, double resolution)? jeu = null)
        => new(
            sousSysteme is { } s ? new SubsystemEvidence("gpu.debit", Evidence.From(s.delta, s.resolution)) : null,
            jeu is { } j ? new GameEvidence("jeu.score", Evidence.From(j.delta, j.resolution)) : null);

    [Fact]
    public void UN_GAIN_SOUS_SYSTEME_SEUL_NE_GARDE_JAMAIS_RIEN()
    {
        // Le test de ce lot. Exactement le resultat du premier autopilote : le banc GPU brille,
        // et personne n'a mesure le jeu. Garder ici, ce serait promettre des FPS sur la foi d'un
        // calcul de shader.
        var d = KeepPolicy.Decide(Preuve(sousSysteme: (2.54, 1.53)), OptimizationGoal.Competitive);

        d.Keep.Should().BeFalse();
        d.RaisonCle.Should().Be(KeepPolicy.CleSousSystemeSeul);
        d.Detail.Should().Contain("aucune mesure en jeu");
    }

    [Fact]
    public void Le_cas_reel_du_brief_gain_GPU_prouve_mais_FPS_non_resolu()
    {
        // « debit GPU +2,6 % prouve · FPS +0,8 % pour ±3,5 % » -> NON RESOLU -> on ne garde pas.
        var d = KeepPolicy.Decide(
            Preuve(sousSysteme: (2.6, 0.3), jeu: (0.8, 3.5)),
            OptimizationGoal.Competitive);

        d.Keep.Should().BeFalse();
        d.RaisonCle.Should().Be(KeepPolicy.CleSousSystemeSeul);
        // Et la phrase dit LES deux choses : le levier fait quelque chose, mais pas quelque chose
        // que le joueur ressentira. « Ca marche, mais pas pour toi » est une reponse utile.
        d.Detail.Should().Contain("+2,6").And.Contain("+0,8").And.Contain("3,5");
    }

    [Fact]
    public void Le_second_cas_du_brief_le_jeu_s_ameliore_vraiment_donc_KEEP()
    {
        // « score jeu +3,1 % pour ±0,7 %, stabilite OK » -> KEEP.
        var d = KeepPolicy.Decide(
            Preuve(sousSysteme: (2.4, 0.3), jeu: (3.1, 0.7)),
            OptimizationGoal.Competitive);

        d.Keep.Should().BeTrue();
        d.RaisonCle.Should().Be(KeepPolicy.CleGainProuve);
        d.Detail.Should().Contain("+3,1");
    }

    [Fact]
    public void Une_REGRESSION_en_jeu_annule_meme_si_le_sous_systeme_gagne()
    {
        // Le piege inverse : la carte calcule plus vite et le jeu va moins bien. Ca arrive — un
        // offset qui fait chauffer, un pilote qui reagit. Le jeu decide.
        var d = KeepPolicy.Decide(
            Preuve(sousSysteme: (2.4, 0.3), jeu: (-4.0, 1.0)),
            OptimizationGoal.Competitive);

        d.Keep.Should().BeFalse();
        d.RaisonCle.Should().Be(KeepPolicy.CleRegression);
    }

    [Fact]
    public void Une_experience_INVALIDE_ne_garde_rien_et_dit_pourquoi()
    {
        var invalide = new PerformanceEvidence(null,
            new GameEvidence("jeu.score", Evidence.Invalide("perte de focus pendant la passe APRES")));

        var d = KeepPolicy.Decide(invalide, OptimizationGoal.Competitive);

        d.Keep.Should().BeFalse();
        d.RaisonCle.Should().Be(KeepPolicy.CleInvalide);
        d.Detail.Should().Contain("focus");
    }

    [Fact]
    public void Aucune_mesure_du_tout_ne_garde_rien()
    {
        var d = KeepPolicy.Decide(new PerformanceEvidence(null, null), OptimizationGoal.Competitive);

        d.Keep.Should().BeFalse();
        d.RaisonCle.Should().Be(KeepPolicy.CleAucuneMesure);
    }

    [Fact]
    public void Un_jeu_prouve_SANS_mesure_de_sous_systeme_suffit_a_garder()
    {
        // Le sous-systeme est un pre-filtre, pas une condition. Un reglage dont on ne sait pas
        // expliquer le mecanisme mais qui ameliore le jeu de facon prouvee reste un bon reglage.
        var d = KeepPolicy.Decide(Preuve(jeu: (3.0, 0.8)), OptimizationGoal.Competitive);

        d.Keep.Should().BeTrue();
    }

    // Deux regles de DecideParMetrique que des mutations laissaient vertes.

    [Fact]
    public void Une_grandeur_invalide_interdit_le_keep_meme_avec_un_gain_de_queue_ailleurs()
    {
        // Une experience qui n'a pas tenu ne dit rien, sur aucune grandeur : un p95 « prouve » tire de la
        // meme experience ne vaut pas mieux.
        var preuves = new[]
        {
            new MetricEvidence(GameMetricKind.P95Frametime, Evidence.From(5.0, 1.0)),
            new MetricEvidence(GameMetricKind.Moyenne, Evidence.Invalide("scene interrompue")),
        };

        var d = KeepPolicy.DecideParMetrique(preuves, OptimizationGoal.Competitive);

        d.Keep.Should().BeFalse();
        d.RaisonCle.Should().Be(KeepPolicy.CleInvalide);
    }

    [Fact]
    public void Aucune_preuve_dit_aucune_mesure_et_non_rien_au_dessus_du_bruit()
    {
        var d = KeepPolicy.DecideParMetrique(Array.Empty<MetricEvidence>(), OptimizationGoal.Competitive);
        d.Keep.Should().BeFalse();
        d.RaisonCle.Should().Be(KeepPolicy.CleAucuneMesure);
    }

    // Deux grandeurs de queue jugées chacune à 95 % : un levier inerte passe pour un gain environ une fois sur vingt. Seule
    // prouvée, une grandeur doit dépasser sa marge du facteur de Bonferroni ; les deux prouvées se confirment.
    [Fact]
    public void Une_seule_queue_prouvee_au_ras_de_sa_marge_ne_garde_pas_le_levier()
    {
        static IReadOnlyList<MetricEvidence> Preuves(double low, double p95) =>
        [
            new(GameMetricKind.UnPourCentLow, Evidence.From(low, 1.0)),
            new(GameMetricKind.P95Frametime, Evidence.From(p95, 1.0)),
            new(GameMetricKind.Moyenne, Evidence.From(0.2, 1.0)),
        ];

        KeepPolicy.DecideParMetrique(Preuves(1.1, 0.3), OptimizationGoal.Competitive).Keep.Should().BeFalse();
        KeepPolicy.DecideParMetrique(Preuves(1.3, 0.3), OptimizationGoal.Competitive).Keep.Should().BeTrue("au-delà du facteur");
        KeepPolicy.DecideParMetrique(Preuves(1.1, 1.1), OptimizationGoal.Competitive).Keep.Should().BeTrue("les deux queues se confirment");
    }
}
