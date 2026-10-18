using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// « Envoyer mon rapport » ouvre le modèle de ticket VIDE, exprès (LienDeRapport.Construire) : processeur, carte et texte
// restent sur la machine jusqu'à ce que le joueur les joigne. Le bouton disait « ticket pré-rempli ».
public class EnvoyerLeRapportDitCeQuIlOuvreTests
{
    private static string Valeur(string fichier, string cle)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var chemin = Path.Combine(d!.FullName, "src", "DrDxt.Infrastructure", "Resources", fichier);
        return XDocument.Load(chemin).Root!.Elements("data").Single(x => x.Attribute("name")!.Value == cle).Element("value")!.Value;
    }

    [Theory]
    [InlineData("Strings.fr.resx", "modèle vide", "joins", "pré-rempli")]
    [InlineData("Strings.resx", "empty", "attach", "prefilled")]
    public void Le_bouton_dit_que_le_ticket_s_ouvre_vide_et_que_le_rapport_se_joint(string fichier, string vide, string joindre, string faux)
    {
        Valeur(fichier, "Verifier.Envoyer").Should().Contain(vide).And.Contain(joindre).And.NotContain(faux);
    }
}
