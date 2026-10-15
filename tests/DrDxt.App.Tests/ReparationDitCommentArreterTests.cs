using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Une fois « Réparer » lancé, l'écran dit comment l'interrompre.
// La réparation tourne dans une console de Microsoft, élevée, hors de DrDXT : tuer DISM en pleine écriture
// depuis l'appli n'est pas un geste sûr. L'état « en cours » donne donc la seule issue propre : fermer la console.
public class ReparationDitCommentArreterTests
{
    private static string Valeur(string fichier, string cle)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var chemin = Path.Combine(d!.FullName, "src", "DrDxt.Infrastructure", "Resources", fichier);
        return XDocument.Load(chemin).Root!.Elements("data").Single(x => x.Attribute("name")!.Value == cle).Element("value")!.Value;
    }

    [Theory]
    [InlineData("Strings.fr.resx", "ferme la console")]
    [InlineData("Strings.resx", "close the console")]
    public void La_reparation_lancee_dit_comment_l_arreter(string fichier, string issue)
    {
        Valeur(fichier, "Reparation.Windows.Lancee").Should().Contain(issue);
    }

    // « sans geste proposé » en tête des états lus d'une page rouge passerait pour « rien à faire ».
    // Ces lignes informent ; les constats, plus bas, gardent leur remède.
    [Theory]
    [InlineData("Strings.fr.resx", "pour information", "sans geste")]
    [InlineData("Strings.resx", "for information", "no action")]
    public void Les_etats_lus_se_disent_pour_information(string fichier, string attendu, string banni)
    {
        foreach (var cle in new[] { "Carte.Verifies.Un", "Carte.Verifies.Plusieurs" })
            Valeur(fichier, cle).Should().Contain(attendu).And.NotContain(banni);
    }
}
