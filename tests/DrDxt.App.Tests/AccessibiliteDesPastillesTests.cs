using System.Globalization;
using DrDxt.App.Converters;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// La gravité d'une ligne ne se porte pas par la couleur seule : la pastille a un mot, et ce mot suit la même règle que sa
// couleur (corail = à corriger, ambre = à regarder, platine = proposé ou information). Un bouton répété d'une ligne à l'autre
// s'annonce avec le titre de sa ligne.
public sealed class AccessibiliteDesPastillesTests
{
    private static EntreeCarte Entree(Severity gravite, bool propose = false)
        => new("Carte graphique", gravite, "Titre", "Détail", "", "", NonLu: false, Propose: propose);

    public static TheoryData<object, string> Cas() => new()
    {
        { Entree(Severity.Major), "CG.ACorriger" },
        { Entree(Severity.Minor), "CG.ACorriger" },
        { Entree(Severity.Info), "CG.ARegarder" },
        { Entree(Severity.Info, propose: true), "CG.Propose" },
        { Severity.Major, "CG.ACorriger" },
        { Severity.Info, "CG.Info" },
    };

    [Theory]
    [MemberData(nameof(Cas))]
    public void Le_mot_de_la_pastille_suit_sa_couleur(object valeur, string cle)
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var mot = new SeverityLabelConverter().Convert(valeur, typeof(string), null!, CultureInfo.InvariantCulture);
        var couleur = new SeverityColorConverter().Convert(valeur, typeof(object), null!, CultureInfo.InvariantCulture);

        mot.Should().Be(Docteur.T(cle));
        var memeCouleur = Cas().Select(c => (object[])c).Where(c => Docteur.T((string)c[1]) == (string)mot)
            .Select(c => new SeverityColorConverter().Convert(c[0], typeof(object), null!, CultureInfo.InvariantCulture)).Distinct();
        memeCouleur.Should().ContainSingle("un même mot a toujours la même couleur").Which.Should().Be(couleur);
    }

    // En titre de groupe, le mot prend sa majuscule (« À corriger ») ; en ligne et pour le lecteur d'écran, il reste tel quel.
    [Fact]
    public void En_titre_de_groupe_le_mot_prend_sa_majuscule()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = new SeverityLabelConverter();
        var mot = (string)c.Convert(Severity.Major, typeof(string), null!, CultureInfo.InvariantCulture);

        c.Convert(Severity.Major, typeof(string), "Titre", CultureInfo.InvariantCulture)
            .Should().Be(char.ToUpper(mot[0], CultureInfo.GetCultureInfo("fr-FR")) + mot[1..]);
        mot[0].Should().Be(char.ToLower(mot[0]), "témoin : la clé est en minuscule");
    }

    [Fact]
    public void Un_bouton_repete_s_annonce_avec_le_titre_de_sa_ligne()
    {
        using (Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR")))
            new NomAccessibleConverter().Convert(["Pourquoi ?", "Le mode Jeu"], typeof(string), null!, CultureInfo.InvariantCulture)
                .Should().Be("Pourquoi ? : Le mode Jeu");
        using (Docteur.Portee(CultureInfo.GetCultureInfo("en-US")))
            new NomAccessibleConverter().Convert(["Why?", "Game Mode"], typeof(string), null!, CultureInfo.InvariantCulture)
                .Should().Be("Why?: Game Mode");
    }
}
