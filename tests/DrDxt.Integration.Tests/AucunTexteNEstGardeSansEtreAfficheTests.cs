using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// Deux nettoyages le meme soir : 26 textes que plus rien n'affichait, dont une promesse fausse
// (« GPU AMD : optimisation bientot disponible »), les bandes et verdicts de l'ancien score (« en excellente
// sante ») et le gabarit « ~{0}% » des gains estimes. Un texte mort n'est pas inoffensif : c'est une phrase
// prete a revenir au premier branchement distrait, sans que personne la relise.
//
// Un texte est vivant si le produit le nomme, ou nomme sa famille (cle construite : « Carte.Etat.{x} »,
// prefixe constant « Bios.Loop.Setting. »). Sinon il sort, ou il entre ci-dessous avec sa raison.
// -----------------------------------------------------------------------
public sealed class AucunTexteNEstGardeSansEtreAfficheTests
{
    private static readonly Dictionary<string, string> GardesSciemment = new(StringComparer.Ordinal)
    {
        ["Erreur."] = "phrase d'un code d'échec, composée par son code (RaisonLisible.Dire)",
        ["NonNuisance."] = "relus par la garde qui les relie a docs/NON-NUISANCE.md, une source pour chaque promesse",
        ["Reglage.nvidia."] = "nom d'un reglage suivi, compose par son identifiant (ScanViewModel.Parties, ReleverReglagesSuivis)",
        ["Reglage.windows."] = "nom d'un reglage suivi, compose par son identifiant (ScanViewModel.Parties, ReleverReglagesSuivis)",
        ["Reglage.memoire."] = "nom d'un reglage suivi, compose par son identifiant (ScanViewModel.Parties, ReleverReglagesSuivis)",
        ["Reglage.bios."] = "nom d'un reglage suivi, compose par son identifiant (ScanViewModel.Parties, ReleverReglagesSuivis)",
        ["Common_"] = "donnees des tests du traducteur (langue par defaut, repli)",
        ["Scan_Title"] = "donnee des tests du traducteur",
    };

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static string SansCommentaires(string texte)
        => System.Text.RegularExpressions.Regex.Replace(texte, @"/\*.*?\*/|<!--.*?-->|(?<!:)//[^\n]*", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);

    [Fact]
    public void Chaque_texte_est_affiche_par_le_produit_ou_garde_avec_sa_raison()
    {
        var racine = Racine();
        var cles = XDocument.Load(Path.Combine(racine.FullName, "src", "DrDxt.Infrastructure", "Resources", "Strings.resx"))
            .Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToList();
        var src = Path.Combine(racine.FullName, "src");
        // Relecture adverse : un nom cite dans un commentaire comptait comme un usage, et deux verdicts
        // de l'ancien score (UI.Verdict.Good, UI.Verdict.Fair) survivaient ainsi. Les commentaires sont retires
        // avant la recherche ; un « // » precede de « : » est une adresse, pas un commentaire.
        var code = string.Join("\n", FichiersDuDepot.Sous(src, "*.cs").Concat(FichiersDuDepot.Sous(src, "*.xaml"))
            .Select(f => SansCommentaires(File.ReadAllText(f))));

        cles.Should().HaveCountGreaterThan(400, "témoin : le fichier des textes est bien lu");
        code.Length.Should().BeGreaterThan(1_000_000, "témoin : le code du produit est bien lu");

        bool Vivant(string cle)
        {
            if (code.Contains(cle, StringComparison.Ordinal)) return true;
            var point = cle.LastIndexOf('.');
            if (point < 0) return false;
            var famille = cle[..point];
            // Une famille ne vit que si la cle est construite : prefixe constant ferme (« "Bios.Loop.Setting." + x »)
            // ou interpolee. Une cle voisine ecrite en entier ne rend pas vivantes ses soeurs.
            return code.Contains($"\"{famille}.\"", StringComparison.Ordinal)
                   || code.Contains($"$\"{famille}.{{", StringComparison.Ordinal);
        }

        cles.Where(c => !Vivant(c) && !GardesSciemment.Keys.Any(g => c.StartsWith(g, StringComparison.Ordinal)))
            .Should().BeEmpty("un texte que rien n'affiche sort du produit, ou entre dans la liste avec sa raison");
    }
}
