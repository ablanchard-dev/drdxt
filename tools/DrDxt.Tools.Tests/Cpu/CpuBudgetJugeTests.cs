using DrDxt.Measurement.Cpu;
using DrDxt.Tools.CpuBench;
using FluentAssertions;
using Xunit;

namespace DrDxt.Tools.Tests.Cpu;

/// <summary>
/// LES TROIS MENSONGES DU VERDICT, TRANSFORMES EN TESTS.
///
/// Chacun de ces cas a ete trouve par une revue, corrige, puis verifie a la main par mutation.
/// Une garde prouvee par une mutation manuelle est une garde prouvee UNE FOIS : la prochaine
/// simplification bien intentionnee la retirera sans que rien ne rougisse. Ils vivent ici pour que
/// la preuve se rejoue toute seule.
/// </summary>
public class CpuBudgetJugeTests
{
    /// <summary>Un point de courbe. Par defaut tout va bien : epinglage complet, meme charge.</summary>
    private static CpuBenchResult Point(int threads, double mops, double dispersion = 0.5,
        ulong somme = 0xABCD, int? epingles = null)
        => new(threads, mops, dispersion, somme, 4, true, 35, epingles ?? threads);

    [Fact]
    public void Un_epinglage_incomplet_sur_UNE_seule_mesure_suffit_a_tout_rendre_indetermine()
    {
        // Le cas exact releve : la passe intermediaire a rate son epinglage, donc ce debit
        // a ete mesure sur un cœur choisi par l'ordonnanceur, pas par nous.
        var verdict = CpuBudgetJuge.Juger(
            [Point(1, 1800), Point(8, 1600, epingles: 7), Point(1, 1798)],
            coeursDeLaClasseRapide: 8);

        verdict.Issue.Should().Be(CpuBudgetIssue.Indetermine);
        verdict.Empechements.Should().ContainSingle()
            .Which.Should().Contain("7/8", "le manquement se NOMME au lieu de se deviner");
    }

    [Fact]
    public void Sans_topologie_aucun_verdict_meme_avec_une_chute_enorme()
    {
        // Une chute de 40 % sans savoir quels cœurs ont ete charges peut n'etre que du SMT ou des
        // cœurs lents. La conclure serait inventer une cause.
        var verdict = CpuBudgetJuge.Juger(
            [Point(1, 1800), Point(8, 1080), Point(1, 1800)], coeursDeLaClasseRapide: 0);

        verdict.Issue.Should().Be(CpuBudgetIssue.Indetermine);
        verdict.Empechements.Should().ContainSingle().Which.Should().Contain("topologie");
    }

    [Fact]
    public void Un_seul_nombre_de_coeurs_ne_permet_pas_de_conclure_a_l_absence_d_effet()
    {
        // Sans deux points, la chute vaut zero PAR CONSTRUCTION : « aucun effet » serait une
        // tautologie qu'aucune machine ne pourrait dementir.
        var verdict = CpuBudgetJuge.Juger(
            [Point(1, 1800), Point(1, 1799)], coeursDeLaClasseRapide: 8);

        verdict.Issue.Should().Be(CpuBudgetIssue.Indetermine);
        verdict.Empechements.Should().ContainSingle().Which.Should().Contain("deux points");
    }

    [Fact]
    public void Le_point_qui_deborde_sur_les_coeurs_lents_ne_sert_JAMAIS_au_verdict()
    {
        // LE CAS QUI QUADRUPLAIT L'EFFET. Mesure du 09/09 : 1 -> 12 threads donnait -41,0 %,
        // 1 -> 8 threads (meme classe) -10,3 %. Le douzieme point melange le budget cherche et
        // l'heterogeneite des cœurs.
        var verdict = CpuBudgetJuge.Juger(
            [Point(1, 1800), Point(8, 1615), Point(12, 1070), Point(1, 1800)],
            coeursDeLaClasseRapide: 8);

        verdict.Issue.Should().Be(CpuBudgetIssue.BudgetEtabli);
        verdict.ThreadsCompares.Should().Be((1, 8), "le verdict s'arrete a la classe rapide");
        verdict.ChutePercent.Should().BeApproximately(-10.3, 0.5);
        verdict.PointMixte!.Threads.Should().Be(12, "il s'affiche, il ne juge pas");
    }

    [Fact]
    public void Une_HAUSSE_du_debit_n_est_jamais_un_bridage_etabli()
    {
        // LE CAS RELEVE. `Math.Abs(ecart) > seuil` rendait « budget limitant etabli »
        // pour une hausse de 10 %. L'outil d'origine ne le montrait pas — il n'imprimait la phrase
        // du budget que si l'ecart etait negatif — et l'extraction a perdu la distinction.
        var verdict = CpuBudgetJuge.Juger(
            [Point(1, 1800), Point(8, 1980), Point(1, 1800)], coeursDeLaClasseRapide: 8);

        verdict.Issue.Should().NotBe(CpuBudgetIssue.BudgetEtabli,
            "un debit qui MONTE ne peut pas prouver qu'un budget bride la machine");
        verdict.Issue.Should().Be(CpuBudgetIssue.Indetermine);
        verdict.ChutePercent.Should().BeApproximately(10, 0.5);
        verdict.Empechements.Should().ContainSingle().Which.Should().Contain("MONTE",
            "une hausse accuse le protocole, et se dit au lieu d'etre rangee dans « aucun effet »");
    }

    [Fact]
    public void Une_hausse_SOUS_le_bruit_reste_une_absence_d_effet_bornee()
    {
        // Une petite hausse indiscernable du bruit n'accuse rien : c'est du bruit, et le verdict
        // reste la borne. Sans ce test, corriger la hausse aurait pu rendre TOUTE hausse suspecte.
        var verdict = CpuBudgetJuge.Juger(
            [Point(1, 1800, dispersion: 0.5), Point(8, 1810, dispersion: 0.5), Point(1, 1800, dispersion: 0.5)],
            coeursDeLaClasseRapide: 8);

        verdict.Issue.Should().Be(CpuBudgetIssue.AucunEffetAuDessusDuSeuil);
    }

    [Fact]
    public void Une_mesure_trop_bruitee_rend_indetermine_et_JAMAIS_rassurant()
    {
        // Le premier mensonge : au-dessus du bruit « ca bride », en dessous « rien ne bride ».
        // Une chute de 4 % avec 9 % de dispersion passait pour « le debit ne bouge pas ».
        var verdict = CpuBudgetJuge.Juger(
            [Point(1, 1800, dispersion: 9), Point(8, 1728, dispersion: 9), Point(1, 1800, dispersion: 9)],
            coeursDeLaClasseRapide: 8);

        verdict.Issue.Should().Be(CpuBudgetIssue.Indetermine,
            "un bruit de 9 % n'exclut meme pas un effet de 27 % : se taire est la seule reponse vraie");
        verdict.Issue.Should().NotBe(CpuBudgetIssue.AucunEffetAuDessusDuSeuil);
    }

    [Fact]
    public void Une_absence_d_effet_se_dit_comme_une_BORNE_chiffree()
    {
        var verdict = CpuBudgetJuge.Juger(
            [Point(1, 1800, dispersion: 0.4), Point(8, 1795, dispersion: 0.4), Point(1, 1799, dispersion: 0.4)],
            coeursDeLaClasseRapide: 8);

        verdict.Issue.Should().Be(CpuBudgetIssue.AucunEffetAuDessusDuSeuil);
        verdict.SeuilPercent.Should().BeGreaterThan(0,
            "« aucun effet » sans le seuil qui le borne serait une absence, pas une mesure");
    }

    [Fact]
    public void Des_sommes_de_controle_divergentes_interdisent_toute_comparaison()
    {
        var verdict = CpuBudgetJuge.Juger(
            [Point(1, 1800, somme: 1), Point(8, 1600), Point(1, 1798, somme: 2)],
            coeursDeLaClasseRapide: 8);

        verdict.Issue.Should().Be(CpuBudgetIssue.ChargesIncomparables,
            "a nombre de threads egal, un travail different rend les debits incomparables");
    }

    [Fact]
    public void Un_budget_reel_est_etabli_quand_il_depasse_franchement_le_bruit()
    {
        var verdict = CpuBudgetJuge.Juger(
            [Point(1, 1817), Point(8, 1615), Point(1, 1817)], coeursDeLaClasseRapide: 8);

        verdict.Issue.Should().Be(CpuBudgetIssue.BudgetEtabli);
        verdict.ChutePercent.Should().BeLessThan(0);
        verdict.Empechements.Should().BeEmpty();
    }
}
