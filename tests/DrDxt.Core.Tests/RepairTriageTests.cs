using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Ce tri decide de ce qu'un joueur voit.
///
/// L'audit rend une quarantaine de constats sur onze domaines ; un decompte de statuts
/// (« 2 recommandations, 6 Unknown ») serait exact et illisible, sans dire quoi regarder. Un rapport que personne ne lit ne vaut pas mieux qu'un rapport faux.
///
/// La regression que ces tests empechent : qu'un constat porteur d'un GESTE se retrouve range
/// parmi les angles morts et disparaisse de l'ecran.
/// </summary>
public class RepairTriageTests
{
    private static RepairFinding C(RepairStatus statut, RepairAction action = RepairAction.Aucune) =>
        new("D", "n", "v", null, statut, "explication", action);

    [Fact]
    public void UN_CONSTAT_PORTEUR_D_UN_GESTE_PASSE_EN_ATTENTION_MEME_S_IL_EST_INCONNU()
    {
        // Le cas reel. Les quatre reglages BIOS ont une valeur `Unknown` — on ne sait pas les
        // lire — et proposent pourtant chacun un passage guide. Les ranger dans les angles morts
        // les rendrait invisibles a celui qui pourrait agir.
        var t = RepairTriage.Trier([C(RepairStatus.Unknown, RepairAction.GuidedBios)]);

        t.Attention.Should().HaveCount(1);
        t.NonLus.Should().BeEmpty();
    }

    [Fact]
    public void UN_INCONNU_SANS_GESTE_EST_UN_ANGLE_MORT_ET_NON_UN_ETAT_VERIFIE()
    {
        // La distinction qui empeche l'audit de se faire passer pour exhaustif. « Je n'ai pas
        // pu lire » ne doit jamais se fondre dans « tout va bien ».
        var t = RepairTriage.Trier([C(RepairStatus.Unknown)]);

        t.NonLus.Should().HaveCount(1);
        t.Verifies.Should().BeEmpty();
        t.Attention.Should().BeEmpty();
    }

    [Theory]
    [InlineData(RepairStatus.Suspicious)]
    [InlineData(RepairStatus.KnownHarmful)]
    public void UN_ETAT_SUSPECT_OU_NUISIBLE_REMONTE_MEME_SANS_GESTE_PROPOSE(RepairStatus s)
    {
        // Le produit n'a pas toujours de remede a offrir, et il doit quand meme le dire. Deux
        // constats de stabilite sont exactement dans ce cas : signature repetee du noyau
        // graphique et vidages qui echouent, sans aucune reparation possible.
        RepairTriage.Trier([C(s)]).Attention.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(RepairStatus.ExpectedDefault)]
    [InlineData(RepairStatus.Observed)]
    [InlineData(RepairStatus.NonDefault)]
    [InlineData(RepairStatus.UserChoiceProbable)]
    public void CE_QUI_EST_LU_ET_NE_DEMANDE_RIEN_EST_COMPTE_ET_NON_DETAILLE(RepairStatus s)
    {
        // Y compris `NonDefault` : « different » n'est pas « endommage ». Le faire remonter en
        // attention transformerait chaque machine personnalisee en champ de ruines — c'est la
        // regle qui gouverne tout l'audit.
        var t = RepairTriage.Trier([C(s)]);

        t.Verifies.Should().HaveCount(1);
        t.Attention.Should().BeEmpty();
    }

    [Fact]
    public void LA_PHRASE_NOMME_LES_ANGLES_MORTS_MEME_QUAND_RIEN_NE_CLOCHE()
    {
        // « Rien a signaler » sans dire combien de choses n'ont pas pu etre lues serait la meme
        // faute que de rendre zero pour un compteur illisible.
        var t = RepairTriage.Trier([C(RepairStatus.Observed), C(RepairStatus.Unknown)]);

        t.Phrase.Should().Contain("rien ne demande ton attention").And.Contain("mais 1 n'a pas pu être lu")
            .And.Contain("états vérifiés").And.NotContain("—");
    }

    [Fact]
    public void LA_PHRASE_S_ACCORDE_AU_SINGULIER()
    {
        // « 1 état(s) méritent … 1 n'ont pas pu être lus » sur la machine de reference.
        var t = RepairTriage.Trier([C(RepairStatus.KnownHarmful), C(RepairStatus.Observed), C(RepairStatus.Unknown)]);

        t.Phrase.Should().Be("1 à corriger, 1 est vérifié, 1 n'a pas pu être lu");
    }

    // Pas « 17 points à corriger » en titre et « 21 états méritent ton attention » dans la phrase : la pile
    // compte aussi les gestes proposés sur un état sain. Elle parle avec les mots du
    // titre (« à corriger » = nuisible ou suspect, la même règle que GraviteDe) et nomme le reste « à regarder ».
    [Fact]
    public void LA_PHRASE_SEPARE_CE_QUI_EST_A_CORRIGER_DE_CE_QUI_EST_A_REGARDER()
    {
        var t = RepairTriage.Trier([C(RepairStatus.KnownHarmful), C(RepairStatus.Suspicious),
            C(RepairStatus.Observed, RepairAction.Recommandation), C(RepairStatus.Observed)]);

        t.Phrase.Should().Be("2 à corriger · 1 à regarder, 1 est vérifié");
        RepairTriage.Trier([C(RepairStatus.Unknown, RepairAction.Recommandation)]).Phrase
            .Should().StartWith("1 à regarder").And.NotContain("à corriger");
    }

    [Fact]
    public void UNE_LISTE_VIDE_NE_CASSE_RIEN()
    {
        var t = RepairTriage.Trier([]);

        t.Attention.Should().BeEmpty();
        t.Phrase.Should().Contain("rien ne demande ton attention");
    }
}
