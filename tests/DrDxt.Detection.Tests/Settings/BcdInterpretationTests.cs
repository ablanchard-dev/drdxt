using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

/// <summary>
/// Un chemin que la machine de reference n'exerce jamais.
///
/// Aucun element de la famille horloge n'est pose sur cette machine (releve le 08/09/2026 :
/// `bcdedit /enum {current}` rend 21 lignes et aucune ne les contient). Le code qui les juge n'y
/// tourne donc jamais ; ces tests sont la seule chose qui l'exerce.
///
/// La regression qu'ils empechent est double : qu'un futur lot cesse de reconnaitre ces elements
/// (ils retomberaient dans le repli « je ne sais pas juger », muets), ou au contraire qu'il se
/// mette a chiffrer un effet que rien n'a mesure.
/// </summary>
public class BcdInterpretationTests
{
    // L'écran de récupération a sa carte dans DrDXT : le geste la nomme. Les éléments d'horloge n'en ont pas :
    // le geste est la commande, sur l'élément lu, à part de l'explication qui reste une phrase.
    [Fact]
    public void Le_geste_se_lit_a_part_et_nomme_l_element_lu()
    {
        BcdInterpretation.Juger("bootstatuspolicy", "IgnoreAllFailures").Remede
            .Should().StartWith("1) ").And.Contain("Rendre l'écran de récupération à Windows").And.Contain("Annuler");

        var horloge = BcdInterpretation.Juger("useplatformclock", "Yes");
        horloge.Remede.Should().StartWith("1) ").And.Contain("bcdedit /deletevalue \"{current}\" useplatformclock", "PowerShell lit {current} sans guillemets comme un bloc de code");
        horloge.Explication.Should().NotContain("bcdedit");
    }

    [Theory]
    [InlineData("useplatformclock")]
    [InlineData("useplatformtick")]
    [InlineData("disabledynamictick")]
    [InlineData("tscsyncpolicy")]
    public void LIRE_UN_ELEMENT_DE_LA_FAMILLE_HORLOGE_PROUVE_QUE_QUELQU_UN_L_A_POSE(string cle)
    {
        // L'argument qui ne demande aucun defaut : `bcdedit /enum` ne liste que ce qui existe
        // dans le magasin. En lire un suffit donc a conclure, sans connaitre la valeur d'origine —
        // c'est une propriete de l'outil, pas une supposition.
        var f = BcdInterpretation.Juger(cle, "Yes");

        f.Statut.Should().Be(RepairStatus.NonDefault);
        f.ValeurAttendue.Should().Be("élément absent");
        f.Explication.Should().Contain("Windows ne pose pas");
    }

    [Theory]
    [InlineData("bootstatuspolicy", "IgnoreAllFailures")]
    [InlineData("hypervisorlaunchtype", "Auto")]
    [InlineData("useplatformclock", "Yes")]
    [InlineData("increaseuserva", "3072")]
    [InlineData("inconnu", "x")]
    public void L_EXPLICATION_SE_LIT_COMME_UNE_PHRASE_ET_PAS_COMME_UNE_NOTE_TECHNIQUE(string cle, string valeur)
    {
        // Le joueur lisait `bcdedit /enum`, des mots en capitales et des pictogrammes.
        var texte = BcdInterpretation.Juger(cle, valeur).Explication ?? string.Empty;

        texte.Should().NotContainAny("`", "bcdedit", "\u26A0");
        System.Text.RegularExpressions.Regex.IsMatch(texte, @"\b[A-ZÉÈÀ]{4,}\b").Should().BeFalse(texte);
    }

    [Theory]
    [InlineData("USEPLATFORMCLOCK")]
    [InlineData("UsePlatformClock")]
    public void LA_CLE_EST_RECONNUE_QUELLE_QUE_SOIT_SA_CASSE(string cle)
    {
        // `bcdedit` n'a jamais promis une casse stable. Une comparaison sensible a la casse
        // ferait retomber l'element dans le repli muet, sans qu'aucune exception ne le signale.
        BcdInterpretation.Juger(cle, "Yes").Statut.Should().Be(RepairStatus.NonDefault);
    }

    [Fact]
    public void AUCUNE_EXPLICATION_DE_LA_FAMILLE_HORLOGE_NE_PROMET_UN_GAIN()
    {
        // La garde qui compte le plus. Ces elements sont au cœur des « tweaks horloge » qu'on
        // recopie partout avec des gains inventes. Le produit dit ce que l'element fait et que le
        // retirer rend la decision a Windows — jamais qu'il rapporte des FPS.
        var f = BcdInterpretation.Juger("useplatformclock", "Yes");

        f.Explication.Should().NotContainAny("%", "FPS", "gain de", "plus rapide", "ameliore");
        f.Explication.Should().Contain("rend la décision à Windows").And.Contain("au débogage");
    }

    [Fact]
    public void LA_POLITIQUE_DE_DEMARRAGE_QUI_CACHE_LES_ECHECS_EST_SUSPECTE()
    {
        // Le cas reel de cette machine, et le seul de la section qui merite une recommandation :
        // il ne degrade pas les performances, il cache les symptomes.
        var f = BcdInterpretation.Juger("bootstatuspolicy", "IgnoreAllFailures");

        f.Statut.Should().Be(RepairStatus.Suspicious);
        f.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void UNE_ENTREE_INCONNUE_EST_LUE_SANS_ETRE_ACCUSEE()
    {
        // Le controle NEGATIF, et il est essentiel : `bcdedit /enum` rend une vingtaine de
        // lignes qui sont toutes posees par Windows lui-meme (`nx`, `locale`, `osdevice`…).
        // Traiter « present » comme « ajoute par quelqu'un » hors de la famille horloge
        // transformerait chaque machine en champ de ruines.
        var f = BcdInterpretation.Juger("bootmenupolicy", "Standard");

        f.Statut.Should().Be(RepairStatus.Observed);
        f.Action.Should().Be(RepairAction.Aucune);
    }
}
