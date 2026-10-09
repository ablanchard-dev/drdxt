using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le focus observé remplace le focus deviné.
///
/// DrDXT inferait l'alt-tab a partir de la REGULARITE des frametimes : une seconde lente et
/// reguliere ressemble a un plafond hors focus. L'inference se trompe dans les deux sens — un jeu
/// qui rame regulierement passe pour un alt-tab, un alt-tab irregulier passe pour du jeu.
///
/// Ce que ces tests verrouillent, et c'est la regle du produit :
///   * une zone hors focus n'est **jamais** une performance du PC ;
///   * un ralentissement avec le jeu au premier plan est **toujours** une vraie donnee ;
///   * « je n'ai pas pu regarder » ne devient **jamais** « il n'etait pas la ».
/// </summary>
public class FocusObservationTests
{
    private const int Jeu = 4242;
    private const int Navigateur = 1337;

    private static FocusObservation Suite(params int?[] lectures)
    {
        var o = FocusObservation.Aucune;
        foreach (var l in lectures) o = o.Avec(l, Jeu);
        return o;
    }

    [Fact]
    public void Une_passe_entierement_jouee_est_comparable()
    {
        var o = Suite(Jeu, Jeu, Jeu, Jeu, Jeu);

        o.FractionAuPremierPlan.Should().Be(1.0);
        o.PasseComparable.Should().BeTrue();
    }

    [Fact]
    public void Un_ALT_TAB_prolonge_rend_la_passe_incomparable()
    {
        // La moitie de la passe passee ailleurs : ces secondes-la ne disent rien du PC en jeu.
        var o = Suite(Jeu, Jeu, Navigateur, Navigateur, Navigateur);

        o.FractionAuPremierPlan.Should().BeApproximately(0.4, 0.01);
        o.PasseComparable.Should().BeFalse();
    }

    [Fact]
    public void Une_notification_d_une_seconde_ne_condamne_pas_une_passe()
    {
        // Le controle qui empeche la garde d'etre trop zelee : un joueur ne perd pas sa mesure
        // parce qu'une fenetre a clignote.
        var lectures = Enumerable.Repeat((int?)Jeu, 59).Append(Navigateur).ToArray();

        Suite(lectures).PasseComparable.Should().BeTrue();
    }

    [Fact]
    public void UNE_LECTURE_IMPOSSIBLE_NE_COMPTE_NI_POUR_NI_CONTRE()
    {
        // Bureau verrouille, transition, lecture refusee : `null` veut dire « je ne sais pas ».
        // Le compter comme une perte de focus invaliderait des passes parfaitement bonnes.
        var o = Suite(Jeu, null, Jeu, null, Jeu);

        o.Inconnues.Should().Be(2);
        o.FractionAuPremierPlan.Should().Be(1.0, "seules les lectures concluantes comptent");
        o.PasseComparable.Should().BeTrue();
    }

    [Fact]
    public void AUCUNE_observation_du_tout_ne_condamne_rien()
    {
        // Une machine ou le focus n'est pas lisible doit rester mesurable. Unknown ne condamne pas.
        FocusObservation.Aucune.FractionAuPremierPlan.Should().BeNull();
        FocusObservation.Aucune.PasseComparable.Should().BeTrue();
    }

    [Fact]
    public void UN_RALENTISSEMENT_AVEC_LE_JEU_AU_PREMIER_PLAN_RESTE_UNE_VRAIE_DONNEE()
    {
        // La regle qui compte le plus. Le focus ne dit rien de la performance : il dit seulement
        // si la mesure porte sur le jeu. Une passe entierement jouee reste comparable, que le PC y
        // ait ete brillant ou catastrophique — c'est justement ce qu'on veut mesurer.
        Suite(Jeu, Jeu, Jeu).PasseComparable.Should().BeTrue();
    }
}
