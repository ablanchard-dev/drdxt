using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests;

// -----------------------------------------------------------------------
// Relu dans les textes : « GPU AMD détecté : optimisation bientôt disponible ». Aucune ligne du
// produit ne l'affichait plus, et DrDXT n'écrit sur aucune carte graphique dans cette version : c'était une
// promesse fausse en réserve, prête à revenir au premier branchement distrait. Un texte dit ce que le produit
// fait aujourd'hui ; ce qui viendra se dit le jour où il vient.
// -----------------------------------------------------------------------
public sealed class AucunTexteNePrometUneFonctionAVenirTests
{
    private static readonly Regex Promesse = new(
        @"bientôt|prochainement|prochaine version|à venir|coming soon|future version|next version",
        RegexOptions.IgnoreCase);

    public static TheoryData<string> Fichiers() => new() { "Strings.resx", "Strings.fr.resx" };

    [Theory]
    [MemberData(nameof(Fichiers))]
    public void Aucun_texte_ne_promet_une_fonction_a_venir(string fichier)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var valeurs = XDocument.Load(Path.Combine(d!.FullName, "src", "DrDxt.Infrastructure", "Resources", fichier))
            .Root!.Elements("data").Select(e => (Cle: (string?)e.Attribute("name"), Texte: (string?)e.Element("value") ?? ""))
            .ToList();

        valeurs.Should().HaveCountGreaterThan(400, "témoin : le fichier est bien lu");
        valeurs.Where(v => Promesse.IsMatch(v.Texte)).Select(v => v.Cle).Should().BeEmpty();
    }
}
