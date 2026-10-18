using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Sous un constat, la pastille en police de code recopiait la phrase juste au-dessus, et un
// point rouge (à corriger) portait l'étiquette « recommandation ». Les données d'origine ne changent pas (l'historique
// compare Valeur et Titre) : seul l'affichage se tait quand il redit ou se contredit.
public sealed class PastillesSansRedireTests
{
    private static EntreeCarte E(Severity g, string titre, string detail, string valeur, string action = "") =>
        new("graphique", g, titre, detail, valeur, action, NonLu: false);

    [Fact]
    public void La_valeur_deja_dite_au_dessus_ne_se_repete_pas()
    {
        E(Severity.Minor, "Pilote graphique 566.36", "Une version plus récente existe.", "566.36").ValeurAAfficher.Should().BeEmpty();
        E(Severity.Minor, "Mode d'alimentation", "Il est sur « performances maximales ».", "Performances maximales").ValeurAAfficher.Should().BeEmpty();
    }

    [Fact]
    public void Une_valeur_qui_apprend_quelque_chose_reste()
    {
        var e = E(Severity.Minor, "Version du pilote graphique", "Une version plus récente existe.", "566.36 installé → 581.57 disponible");
        e.ValeurAAfficher.Should().Be("566.36 installé → 581.57 disponible");
        e.Valeur.Should().Be("566.36 installé → 581.57 disponible", "la donnée elle-même ne change pas");
        E(Severity.Info, "Enregistrement continu", "Il enregistre en continu.", "environ 5 % d'images en moins").ValeurAAfficher
            .Should().NotBeEmpty("le 5 % n'est pas dans la phrase");
    }

    // « 60 » se retrouvait dans « 1600 », et une valeur courte dans n'importe quel mot du dessus.
    [Fact]
    public void Un_nombre_ou_un_mot_court_dans_un_autre_ne_compte_pas_comme_deja_dit()
    {
        E(Severity.Minor, "Fréquence de l'écran", "La mémoire tourne à 1600 MHz.", "60 Hz").ValeurAAfficher.Should().Be("60 Hz");
        E(Severity.Minor, "Mode jeu", "Le mode jeu de Windows est coupé.", "On").ValeurAAfficher.Should().Be("On");
    }

    // La phrase dit déjà les deux versions : la pastille n'apprendrait rien.
    [Fact]
    public void Des_nombres_tous_deja_dits_au_dessus_ne_se_repetent_pas() =>
        E(Severity.Major, "Version du pilote graphique", "Ton pilote NVIDIA 617.14 a une version de retard : la dernière publiée est la 617.42.",
            "617.14 installé → 617.42 disponible").ValeurAAfficher.Should().BeEmpty();

    [Fact]
    public void Un_point_rouge_ne_dit_pas_recommandation_mais_les_autres_actions_restent()
    {
        E(Severity.Major, "Écran de récupération", "x", "", "recommandation").ActionAAfficher.Should().BeEmpty();
        E(Severity.Info, "Mode jeu", "x", "", "recommandation").ActionAAfficher.Should().BeEmpty("la couleur du point le dit déjà");
        using (Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo("en-US")))
            E(Severity.Major, "Recovery screen", "x", "", "recommendation").ActionAAfficher.Should().BeEmpty("en anglais aussi");
        E(Severity.Info, "Mode jeu", "x", "", "recommendation").ActionAAfficher.Should()
            .BeEmpty("un scan en anglais relu par un écran en français : la pastille reste muette");
        E(Severity.Major, "Ligne", "x", "", "DrDXT peut le réparer").ActionAAfficher.Should().Be("DrDXT peut le réparer");
    }
}
