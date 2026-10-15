using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Un liseré teinté en rouge, orange ou vert est le feu tricolore par défaut de tout gabarit. Les
// références (Vercel : la couleur d'état seulement à l'échelle d'un point ; Linear : étiquette neutre)
// disent la même chose.
//
// La règle : dans le style `PastilleEtat`, un état ne change que la teinte héritée par le POINT
// (`TextElement.Foreground`). Ni liseré, ni fond : l'étiquette reste la même pour tous les états,
// le mot dit l'état, le point le colore.
// -----------------------------------------------------------------------
public sealed class LaCouleurDEtatNeVitQueDansLePointTests
{
    private static XElement StyleDeLaPastille()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var vue = XDocument.Load(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return vue.Descendants().Single(e => e.Name.LocalName == "Style"
                                             && (string?)e.Attribute(x + "Key") == "PastilleEtat");
    }

    [Fact]
    public void Les_etats_sont_bien_lus_dans_le_style()
        => StyleDeLaPastille().Descendants().Where(e => e.Name.LocalName == "DataTrigger")
            .Should().HaveCountGreaterThan(2, "temoin : les trois etats doivent etre vus");

    [Fact]
    public void Un_etat_ne_teinte_que_le_point()
        => StyleDeLaPastille().Descendants()
            .Where(e => e.Name.LocalName == "DataTrigger")
            .SelectMany(t => t.Descendants().Where(s => s.Name.LocalName == "Setter"))
            .Select(s => (string?)s.Attribute("Property"))
            .Where(p => p != "TextElement.Foreground")
            .Should().BeEmpty("la couleur d'etat vit dans le point ; liseré et fond restent neutres");
}
