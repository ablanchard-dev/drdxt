using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, D5.6, revue) : trois des cinq « à faire d'abord » étaient le même constat. Ils deviennent
// une ligne qui les nomme tous ; le domaine garde chacun avec son remède.
public sealed class AFaireRegroupeLesMemesConstatsTests
{
    private static EntreeCarte E(string titre, Severity g = Severity.Major) =>
        new("windows", g, titre, "détail", string.Empty, string.Empty, false);

    [Fact]
    public void Les_constats_de_meme_phrase_d_un_domaine_font_une_ligne_qui_les_nomme()
    {
        var lignes = new List<EntreeCarte>
        {
            E("service A : son fichier peut être remplacé par un autre programme"),
            E("service B : son fichier peut être remplacé par un autre programme"),
            E("tâche planifiée C : son fichier peut être remplacé par un autre programme"),
            E("Écran de récupération au démarrage"),
        };
        var windows = new DomaineMachine("windows", "Windows", "", EtatDomaine.ACorriger, "", "", [], lignes, [], []);

        var aFaire = CarteDeLaMachine.AFaire([windows]);

        aFaire.Should().HaveCount(2);
        aFaire[0].Entree.Titre.Should().Be("Service A, service B, tâche planifiée C : leur fichier peut être remplacé par un autre programme");
        aFaire[1].Entree.Titre.Should().Be("Écran de récupération au démarrage", "un constat différent n'est pas touché");
        windows.Lignes.Should().HaveCount(4, "le domaine garde chaque constat et son remède");
    }

    // 03/10/2026 (D8.3, revue n° 3) : repris dans le verdict après « ici : », un titre ne garde ni sa majuscule de
    // tête ni son deux-points intérieur ; un nom propre en tête reste écrit comme il est.
    [Theory]
    [InlineData("Écran de récupération au démarrage", "écran de récupération au démarrage")]
    [InlineData("service A : son fichier peut être remplacé", "service A, son fichier peut être remplacé")]
    [InlineData("Windows Update en pause", "Windows Update en pause")]
    [InlineData("XMP désactivé", "XMP désactivé")]
    public void Un_titre_repris_dans_une_phrase_n_a_ni_majuscule_ni_double_deux_points(string titre, string attendu) =>
        CarteDeLaMachine.DansLaPhrase(titre).Should().Be(attendu);

    [Fact]
    public void Un_constat_seul_garde_son_titre()
    {
        var windows = new DomaineMachine("windows", "Windows", "", EtatDomaine.ACorriger, "", "", [],
            [E("service A : son fichier peut être remplacé par un autre programme")], [], []);
        CarteDeLaMachine.AFaire([windows]).Single().Entree.Titre.Should().Be("service A : son fichier peut être remplacé par un autre programme");
    }
}
