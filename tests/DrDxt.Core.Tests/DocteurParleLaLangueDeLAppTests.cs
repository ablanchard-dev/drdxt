using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Le docteur écrivait ses phrases en français dans le code : sur un Windows anglais, l'App parlait anglais et le docteur
// français. Ses phrases vivent dans Docteur.resx (anglais) et Docteur.fr.resx ; ces tests gardent les deux fichiers
// d'accord (mêmes clés, mêmes trous {n}) et la langue fixée par l'App.
public sealed class DocteurParleLaLangueDeLAppTests
{
    private static string Ressources()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return Path.Combine(d!.FullName, "src", "DrDxt.Core", "Resources");
    }

    private static Dictionary<string, string> Lire(string fichier) => XDocument.Load(Path.Combine(Ressources(), fichier))
        .Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

    private static string[] Trous(string texte) =>
        Regex.Matches(texte, @"\{(\d+)(:[^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void Les_deux_langues_ont_les_memes_cles_et_les_memes_trous()
    {
        var en = Lire("Docteur.resx");
        var fr = Lire("Docteur.fr.resx");

        en.Should().NotBeEmpty();
        fr.Keys.Should().BeEquivalentTo(en.Keys);
        foreach (var (cle, texte) in en)
            Trous(fr[cle]).Should().Equal(Trous(texte), $"« {cle} » doit porter les mêmes valeurs dans les deux langues");
    }

    [Fact]
    public void Le_francais_par_defaut_l_anglais_quand_l_App_le_fixe()
    {
        using (Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR")))
            Docteur.T("WU.PourInstaller").Should().StartWith("1) Paramètres");
        using (Docteur.Portee(CultureInfo.GetCultureInfo("en-US")))
            Docteur.T("WU.PourInstaller").Should().StartWith("1) Settings");
        using (Docteur.Portee(CultureInfo.GetCultureInfo("de-DE")))
            Docteur.T("WU.PourInstaller").Should().StartWith("1) Settings", "une langue sans traduction lit l'anglais");
    }

    // La phrase dite à la fermeture quand un acquit reste refusé. Une App relancée est un autre processus : elle n'acquitte
    // pas l'entrée de la précédente, « relance DrDXT pour le confirmer » était faux. Ce qui suit est le rejeu, et quand.
    [Theory]
    [InlineData("fr-FR", "relance", "service")]
    [InlineData("en-US", "restart", "service")]
    public void Un_acquit_refuse_dit_le_rendu_jamais_une_relance(string langue, string interdit, string condition)
    {
        using (Docteur.Portee(CultureInfo.GetCultureInfo(langue)))
            Docteur.T("JEA.AcquitRefuse").Should().NotContainEquivalentOf(interdit).And.Contain(condition);
    }
}
