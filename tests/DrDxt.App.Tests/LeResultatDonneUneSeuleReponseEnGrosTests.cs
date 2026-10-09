using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 23/09/2026. Critique Impeccable sur un scan réel, défaut n°2 : sous le titre du résultat, deux
// compteurs au même poids, « 1 problème prouvé · 7 leviers lus sur 7 » puis « 5 point(s) à
// corriger ». Le joueur ne sait pas lequel lire, ni pourquoi 1 et 5 diffèrent.
//
// La règle : une seule réponse en gros. Le résumé (ce qu'il y a à corriger) vient d'abord, dans la
// police d'affichage ; le registre des leviers le suit, en note de méthode.
// -----------------------------------------------------------------------
public sealed class LeResultatDonneUneSeuleReponseEnGrosTests
{
    private static List<XElement> BlocsDuResultat()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var vue = XDocument.Load(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        return vue.Descendants().Where(e => e.Name.LocalName == "TextBlock"
            && Texte(e) is "{Binding ProblemSummary}" or "{Binding RegisterText}").ToList();
    }

    // 29/09/2026 (B3) : le résumé compte jusqu'à sa valeur ; sa liaison passe par effets:Compteur.Texte.
    private static string? Texte(XElement e)
        => (string?)e.Attribute("Text") ?? (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Compteur.Texte");

    [Fact]
    public void Les_deux_compteurs_sont_bien_lus()
        => BlocsDuResultat().Should().HaveCountGreaterThan(1, "temoin : le resume et le registre doivent etre vus");

    [Fact]
    public void Le_resume_vient_d_abord_et_seul_il_est_en_police_d_affichage()
    {
        var blocs = BlocsDuResultat();
        Texte(blocs[0]).Should().Be("{Binding ProblemSummary}");
        ((string?)blocs[0].Attribute("FontFamily")).Should().Be("{StaticResource UiDisplayFont}");
        ((string?)blocs[1].Attribute("FontFamily")).Should().NotBe("{StaticResource UiDisplayFont}",
            "le registre est une note de methode, pas un second titre");
    }
}
