using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Le frere du crible des boutons (AucunBoutonSansTexteNEstAnonymeTests).
//
// Mesure par UI Automation sur l'application lancee : un champ de saisie visible s'annoncait
// « modification », sans nom. Trois dans les vues, pas deux : la « Precision (facultatif) » de la
// scene (ecrite deux fois, au lancement et avant d'optimiser) et le chemin du rapport exporte. Chacun
// a son libelle visible juste au-dessus ; un lecteur d'ecran, lui, ne le relie pas tout seul.
//
// La regle : un champ porte `AutomationProperties.Name` ou `AutomationProperties.LabeledBy`.
// -----------------------------------------------------------------------
public sealed class AucunChampDeSaisieNEstAnonymeTests
{
    private static readonly string[] TypesDeSaisie = { "TextBox", "PasswordBox", "ComboBox" };

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static List<(string Vue, int Ligne)> Champs(bool anonymesSeulement)
    {
        var app = Path.Combine(Racine().FullName, "src", "DrDxt.App");
        var vues = Directory.EnumerateFiles(Path.Combine(app, "Views"), "*.xaml", SearchOption.AllDirectories)
                            .Append(Path.Combine(app, "MainWindow.xaml")).Where(File.Exists);
        var trouves = new List<(string, int)>();
        foreach (var vue in vues)
        {
            var doc = XDocument.Load(vue, LoadOptions.SetLineInfo);
            foreach (var e in doc.Descendants().Where(e => TypesDeSaisie.Contains(e.Name.LocalName)))
            {
                var nomme = e.Attribute("AutomationProperties.Name") is not null
                            || e.Attribute("AutomationProperties.LabeledBy") is not null;
                if (!anonymesSeulement || !nomme)
                    trouves.Add((Path.GetFileName(vue), ((System.Xml.IXmlLineInfo)e).LineNumber));
            }
        }
        return trouves;
    }

    [Fact]
    public void Le_crible_voit_bien_les_champs_de_l_interface()
        => Champs(anonymesSeulement: false).Should().HaveCountGreaterThan(2,
            "temoin : les trois champs de ScanView doivent etre vus");

    [Fact]
    public void Aucun_champ_de_saisie_ne_s_annonce_sans_nom()
        => Champs(anonymesSeulement: true).Select(c => $"{c.Vue}:{c.Ligne}").Should().BeEmpty(
            "un lecteur d'ecran annonce « modification » et rien d'autre : la personne ne sait pas quoi taper");
}
