using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le modele apparie face a des derives qui ne sont pas des droites.
///
/// `PairedAb` ajuste `fps ≈ a + b·t + c·estB` : il retire une derive **lineaire**. La vraie vie en
/// produit d'autres — un plateau thermique, une courbe, un changement brutal de phase, un bruit qui
/// grossit. La question n'est pas d'etre exact partout, c'est de **ne jamais fabriquer une
/// precision qu'on n'a pas**.
///
/// La regle qui gouverne ces tests : quand le modele n'est plus defendable, le verdict doit
/// devenir `NoResolvedChange` ou `Invalid` — jamais un gain confiant. Un instrument qui se trompe
/// en disant « je ne sais pas » reste utilisable ; un instrument qui se trompe en disant « +5 % »
/// fait garder un reglage nuisible.
/// </summary>
public class PairedAbRobustnessTests
{
    private static IReadOnlyList<AbPass> Suite(params (char Etat, double Fps)[] p) =>
        p.Select(x => new AbPass(x.Etat == 'B', x.Fps)).ToList();

    [Fact]
    public void PLATEAU_THERMIQUE_la_machine_chute_puis_se_stabilise()
    {
        // La carte chauffe pendant les deux premieres passes, puis se stabilise a son palier.
        // La derive n'est pas lineaire : elle est concentree au debut.
        var r = PairedAb.Analyse(
            Suite(('A', 150), ('B', 140), ('A', 132), ('B', 131), ('A', 130), ('B', 130)),
            resolutionPct: 2.0);

        // Le reglage ne fait rien ; toute la variation est thermique. Le modele ne doit pas la
        // convertir en effet.
        r.Game!.Preuve.Outcome.Should().NotBe(EvidenceOutcome.ProvenGain,
            "un plateau thermique n'est pas un gain de reglage");
        Math.Abs(r.Game.Preuve.DeltaPct).Should().BeLessThan(4.0,
            "l'effet residuel doit rester petit devant la chute de 13 %");
    }

    [Fact]
    public void DERIVE_COURBE_une_montee_qui_ralentit()
    {
        // Racine carree : ca monte vite puis de moins en moins. Le modele lineaire ne peut pas
        // l'epouser exactement, et c'est justement le cas ou il ne doit pas se croire precis.
        var suite = Suite(
            ('A', 100), ('B', 110), ('A', 117), ('B', 122), ('A', 126), ('B', 130));

        var r = PairedAb.Analyse(suite, resolutionPct: 3.0);

        r.Game!.Preuve.Outcome.Should().NotBe(EvidenceOutcome.ProvenGain,
            "une courbure residuelle ne doit pas passer pour un effet du reglage");
    }

    [Fact]
    public void CHANGEMENT_BRUTAL_DE_PHASE_le_jeu_change_de_scene_au_milieu()
    {
        // Sortie de zone, nouvelle carte : le niveau saute de 30 % d'un coup. Aucun modele lineaire
        // ne decrit ca, et le verdict doit rester prudent.
        var r = PairedAb.Analyse(
            Suite(('A', 100), ('B', 101), ('A', 130), ('B', 131), ('A', 130), ('B', 131)),
            resolutionPct: 2.0);

        // Ce test a trouve un vrai defaut. Avant la garde de desajustement, le modele rendait
        // ici **-5,4 %, « degradation prouvee »** — pour un effet reel de +1 %. Le saut de 30 %,
        // qu'une droite ne peut pas epouser, se deversait dans le coefficient d'etat.
        //
        // Le bon verdict est `Invalid`, et pas « non resolu » : la difference compte. « Non resolu »
        // dit « j'ai regarde et je n'ai rien vu » ; ici la verite est « mon modele ne decrit pas ces
        // donnees », et le message le dit avec ses chiffres.
        r.Game!.Preuve.Outcome.Should().Be(EvidenceOutcome.Invalid);
        r.Game.Preuve.InvalidReason.Should().Contain("résidu").And.Contain("saut de phase");
    }

    [Fact]
    public void BRUIT_HETEROSCEDASTIQUE_la_dispersion_grossit_avec_le_temps()
    {
        // La partie devient de plus en plus irreguliere. Le modele suppose un bruit constant ;
        // il ne doit pas pour autant conclure a un effet.
        var r = PairedAb.Analyse(
            Suite(('A', 100), ('B', 101), ('A', 96), ('B', 106), ('A', 88), ('B', 114)),
            resolutionPct: 3.0);

        r.Game!.Preuve.Outcome.Should().NotBe(EvidenceOutcome.ProvenRegression,
            "un bruit qui grossit n'est pas une degradation");
    }

    [Fact]
    public void UNE_PASSE_PERDUE_ne_decale_pas_le_temps_des_autres()
    {
        // L'index de temps vient de la sequence COMPLETE, pas de la liste filtree. Reboucher le
        // trou ferait mentir la pente — et c'est un piege facile a introduire par refactor.
        var complet = Suite(('A', 100), ('B', 105), ('A', 110), ('B', 115.5), ('A', 120), ('B', 126));
        var trouee = Suite(('A', 100), ('B', 105), ('A', 0), ('B', 115.5), ('A', 120), ('B', 126));

        var a = PairedAb.Analyse(complet, resolutionPct: 1.0).Game!.Preuve;
        var b = PairedAb.Analyse(trouee, resolutionPct: 1.0).Game!.Preuve;

        b.DeltaPct.Should().BeApproximately(a.DeltaPct, 1.5,
            "perdre une passe doit couter de la precision, pas deplacer le resultat");
    }

    [Fact]
    public void Un_effet_REEL_reste_visible_malgre_une_derive_non_lineaire()
    {
        // Le controle qui empeche la prudence de devenir de l'aveuglement : un vrai +8 % doit
        // rester VISIBLE a travers un plateau thermique — le modele ne doit pas le manger en
        // meme temps que la derive.
        var r = PairedAb.Analyse(
            Suite(('A', 150), ('B', 151.2), ('A', 132), ('B', 142.6), ('A', 130), ('B', 140.4)),
            resolutionPct: 2.0);

        r.Game!.Preuve.DeltaPct.Should().BeGreaterThan(3.0);

        // Corrige le 07/09/2026 : « visible » n'est pas « prouve ». Le plateau laisse un gros
        // residu, et sur six passes il ne reste que trois degres de liberte pour l'estimer. Le
        // chiffre sort juste ; le declarer prouve demanderait des alternances supplementaires.
        r.Game.Preuve.Outcome.Should().NotBe(EvidenceOutcome.Invalid,
            "un plateau thermique ne doit pas invalider l'experience, seulement elargir son doute");
    }
}
