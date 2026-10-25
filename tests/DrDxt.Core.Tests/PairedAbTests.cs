using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// A-b-a-b : distinguer « LE PC A CHANGE PARCE QUE LE REGLAGE A CHANGE » DE « … parce que six
/// Minutes se sont ecoulees ».
///
/// Ce n'est pas une precaution theorique. Mesure du 07/09/2026 sur les captures reelles du
/// **deux des trois** ont une derive qui depasse leur propre bruit — 22h07 derive de 4,93 %
/// pour un bruit de 1,83 %, 23h37 de 18,68 % pour 2,36 %. Sur cette machine, en partie reelle, un
/// A/B en AAA→BBB attribue le plus souvent au reglage ce qui n'est que le temps ecoule.
///
/// Le remede est d'apparier dans le TEMPS. Chaque B se compare au A qui le precede immediatement,
/// et la derive s'annule au premier ordre : ce qui reste est l'effet du reglage.
///
/// Il faut au moins deux paires. Une seule (A→B) est exactement le protocole naif, et l'appairer
/// ne le rend pas meilleur — il faut deux paires pour que la derive ait quelque chose a annuler.
/// </summary>
public class PairedAbTests
{
    /// <summary>Une sequence appariee : l'ordre est le protocole, il n'est pas decoratif.</summary>
    private static IReadOnlyList<AbPass> Suite(params (char Etat, double Fps)[] passes) =>
        passes.Select(p => new AbPass(p.Etat == 'B', p.Fps)).ToList();

    // Deux gardes que des mutations laissaient vertes.

    [Fact]
    public void Trois_passes_sont_invalides_et_non_un_simple_changement_non_resolu()
    {
        // A-B-A : corrélation temps/état nulle, donc la garde de protocole laisse passer ; avec trois
        // paramètres pour trois points, il ne reste aucun degré de liberté. Sans la garde des quatre
        // passes, le verdict retombait en « rien de résolu », comme si la mesure avait tenu.
        var r = PairedAb.Analyse(Suite(('A', 100), ('B', 110), ('A', 100)), resolutionPct: 3.0);
        r.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.Invalid);
        r.Game!.Preuve.InvalidReason.Should().Contain("au moins 4 passes");
    }

    // Des passes exactement sur la droite laissent un résidu nul : une marge de zéro rendait « prouvé » un écart de 0,1 %.
    [Fact]
    public void Un_residu_nul_ne_donne_pas_une_marge_nulle()
    {
        var parfaite = Suite(('A', 100), ('B', 100.1), ('B', 100.1), ('A', 100), ('A', 100), ('B', 100.1));

        PairedAb.Analyse(parfaite, resolutionPct: 3.0).Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange,
            "la marge de la scène prend le relais");
        PairedAb.Analyse(parfaite, resolutionPct: double.NaN).Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange,
            "sans bruit connu, le plancher");
        var nette = Suite(('A', 100), ('B', 120), ('B', 120), ('A', 100), ('A', 100), ('B', 120));
        PairedAb.Analyse(nette, resolutionPct: double.NaN).Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenGain,
            "un écart de 20 % passe le plancher");
    }

    [Fact]
    public void Une_sequence_a_un_seul_etat_le_dit()
    {
        var r = PairedAb.Analyse(Suite(('A', 100), ('A', 101), ('A', 99), ('A', 100)), resolutionPct: 3.0);
        r.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.Invalid);
        r.Game!.Preuve.InvalidReason.Should().Contain("un seul état");
    }

    [Fact]
    public void Une_DERIVE_PURE_ne_produit_AUCUN_effet_apparie()
    {
        // Le test qui justifie tout. La machine monte de 10 fps par passe et le reglage ne fait
        // Rien. En AAA→BBB, la moyenne des B depasse celle des A de 30 fps : « +21 % prouve ».
        // Apparie, chaque B vaut son A + 10, donc l'effet mesure est exactement... la derive d'une
        // passe, et non trois. C'est le premier ordre qui s'annule.
        var apparie = PairedAb.Analyse(Suite(('A', 100), ('B', 110), ('A', 120), ('B', 130)), resolutionPct: 3.0);
        var naif = Evidence.From(
            deltaPct: ((110.0 + 130.0) / 2 - (100.0 + 120.0) / 2) / ((100.0 + 120.0) / 2) * 100.0,
            resolutionPct: 3.0);

        naif.Outcome.Should().Be(EvidenceOutcome.ProvenGain, "le protocole naif se fait berner");

        // Exactement zero, pas « petit ». Un appairage successif naif (chaque B contre le A qui
        // le precede) laisserait encore +9,5 % ici — il divise la derive, il ne l'annule pas.
        // La regression avec indicatrice, elle, la retire entierement.
        apparie.Game!.Preuve.DeltaPct.Should().BeApproximately(0.0, 1e-9,
            "une derive pure ne doit produire AUCUN effet apparent");
        apparie.Game.Preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange);
    }

    [Fact]
    public void Un_effet_REEL_survit_a_la_derive()
    {
        // Derive de +10 par passe et un vrai +5 % sur B. L'appairage doit retrouver le +5 %,
        // pas la somme des deux.
        var suite = Suite(('A', 100), ('B', 115.5), ('A', 120), ('B', 136.5));

        var r = PairedAb.Analyse(suite, resolutionPct: 1.0);

        r.Game!.Preuve.DeltaPct.Should().BeApproximately(5.0, 1.0, "le vrai effet, degage de la derive");

        // Ce test exigeait `ProvenGain` et l'obtenait parce que le seuil
        // venait d'un `resolutionPct: 1.0` passe a la main — un chiffre choisi par le test, pas
        // mesure par l'experience. Depuis, l'experience annonce sa resolution, et quatre passes
        // n'ont qu'UN degre de liberte (trois parametres ajustes sur quatre points, t95 = 12,706).
        // Un vrai +5 % n'y est pas prouvable, et le produit doit le dire au lieu de l'affirmer.
        r.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange,
            "quatre passes ne resolvent pas 5 % — il faut des alternances, pas de la conviction");
    }

    [Fact]
    public void Une_REGRESSION_reelle_est_vue_malgre_une_derive_qui_monte()
    {
        // Le cas le plus dangereux : le reglage degrade, mais la partie s'echauffe et monte. Un A/B
        // naif verrait un gain et GARDERAIT un reglage nuisible.
        var suite = Suite(('A', 100), ('B', 95), ('A', 110), ('B', 104.5));

        var r = PairedAb.Analyse(suite, resolutionPct: 1.0);

        r.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenRegression);
        r.RegressionEnJeuProuvee.Should().BeTrue();
    }

    [Fact]
    public void UNE_SEULE_paire_ne_vaut_pas_mieux_que_le_protocole_naif()
    {
        // A→B tout court : il n'y a rien a annuler. Le dire est plus honnete que de faire croire
        // qu'un appairage a eu lieu.
        var r = PairedAb.Analyse(Suite(('A', 100), ('B', 130)), resolutionPct: 1.0);

        r.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.Invalid);
        r.Game!.Preuve.InvalidReason.Should().Contain("passes exploitables");
    }

    [Fact]
    public void Une_sequence_SANS_alternance_est_refusee()
    {
        // AABB n'est pas un protocole apparie : c'est AAA→BBB avec moins de passes. L'accepter
        // ferait passer le pire protocole pour le meilleur.
        //
        // J'ai d'abord cru que le calcul le refuserait tout seul, par colinearite. Faux, verifie :
        // sur quatre passes AABB rend un determinant de 1, pas de 0. Ce qui le trahit est la
        // CORRELATION entre le temps et l'etat — r² = 0,8 pour AABB, 0,2 pour ABAB, 0,0 pour ABBA.
        var r = PairedAb.Analyse(Suite(('A', 100), ('A', 110), ('B', 120), ('B', 130)), resolutionPct: 1.0);

        r.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.Invalid);
    }

    [Fact]
    public void L_ordre_ABBA_est_accepte_aussi()
    {
        // A-b-b-a annule la derive lineaire par symetrie plutot que par appairage successif. Les
        // deux protocoles sont valides ; refuser l'un des deux serait arbitraire.
        // Derive +10 fps par passe, effet +5 % : A=100 a t0, B=(110)x1,05 a t1, B=(120)x1,05 a t2,
        // A=130 a t3. Des chiffres qui ne suivent pas le modele ne testeraient que ma distraction.
        var r = PairedAb.Analyse(Suite(('A', 100), ('B', 115.5), ('B', 126), ('A', 130)), resolutionPct: 1.0);

        r.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenGain);
        r.Game!.Preuve.DeltaPct.Should().BeApproximately(5.0, 1.5);
    }

    [Fact]
    public void Sous_la_resolution_l_appairage_ne_prouve_toujours_RIEN()
    {
        // L'appairage augmente la sensibilite, il ne la rend pas infinie. Un effet sous le bruit
        // reste non resolu — et surtout pas un gain.
        //
        // Mes premiers chiffres etaient (100 · 100,5 · 110 · 110,4) : je les croyais « presque
        // nuls » parce qu'ils se ressemblent. Avec une derive de +10 par passe, un B a 100,5 la ou
        // le A du moment vaut 105 est une vraie regression de -4,3 %, et le calcul l'a dit. Ici :
        // derive +10 par passe, effet +0,4 %, donc B = (A du moment) x 1,004.
        var r = PairedAb.Analyse(Suite(('A', 100), ('B', 110.44), ('A', 120), ('B', 130.52)), resolutionPct: 3.0);

        r.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.NoResolvedChange);
    }

    [Fact]
    public void Une_passe_INEXPLOITABLE_invalide_la_paire_sans_fausser_les_autres()
    {
        // Une passe a zero n'est pas une mesure basse : la paire qui la contient ne compte pas,
        // mais les autres restent bonnes.
        var r = PairedAb.Analyse(
            Suite(('A', 100), ('B', 105), ('A', 0), ('B', 105), ('A', 100), ('B', 105)),
            resolutionPct: 1.0);

        r.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.ProvenGain);
        r.Game!.Preuve.DeltaPct.Should().BeApproximately(5.0, 0.5, "la paire cassee ne doit pas tirer le resultat");
    }
}
