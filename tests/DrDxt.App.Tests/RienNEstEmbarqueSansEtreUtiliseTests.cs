using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 18/09/2026. Ligne « compresse image » de la checklist produit, appliquee cette
// fois a l'application. Sur Lumenia, la meme ligne avait sorti une icone de
// 2,18 Mo servie pour dessiner 180 points. Ici, la mesure dit autre chose, et de
// pire : Deux images sont embarquees dans l'executable et **referencees nulle
// part**.
//
//   Assets/mascot.png  1254x1254, 1 853 Ko
//   Assets/logo.png      560x660,   203 Ko
//
// Aucune balise `<Image>`, aucun `BitmapImage`, aucun pack URI dans tout le code
// et tous les XAML. Un `<Resource Include=...>` est compile dans l'assembly :
// ces 2,0 Mo voyagent dans chaque installeur et sont charges en memoire par WPF,
// pour rien.
//
// Ce test ne supprime rien, et ce n'est pas une precaution de style : retirer
// un fichier du depot est une decision produit. Il fait ce qu'un test peut faire
// honnetement -- rendre la dette VISIBLE et empecher la suivante d'entrer en
// silence. Le jour ou une image trouve son ecran, elle quitte la liste ; le jour
// ou la decision tombe, elle la quitte aussi.
// -----------------------------------------------------------------------
public sealed class RienNEstEmbarqueSansEtreUtiliseTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static string Csproj() => File.ReadAllText(
        Path.Combine(Racine().FullName, "src", "DrDxt.App", "DrDxt.App.csproj"));

    /// <summary>Les ressources compilees dans l'assembly, une par une.</summary>
    /// <remarks>
    /// Les motifs (`Assets\Fonts\*.ttf`) sont ecartes : un glob ne designe pas un
    /// fichier, et l'accuser serait du bruit -- la lecon du crible de chemins du
    /// meme jour, qui accusait une elision a tort.
    /// </remarks>
    private static List<string> RessourcesEmbarquees()
        => Regex.Matches(Csproj(), @"<Resource\s+Include=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Where(c => !c.Contains('*'))
            .ToList();

    /// <summary>Tout le code et tout le balisage livres — pas les artefacts de build.</summary>
    private static string CodeEtBalisage()
    {
        var src = new DirectoryInfo(Path.Combine(Racine().FullName, "src"));
        var fichiers = src.GetFiles("*.xaml", SearchOption.AllDirectories)
            .Concat(src.GetFiles("*.cs", SearchOption.AllDirectories))
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        return string.Join('\n', fichiers.Select(f => File.ReadAllText(f.FullName)));
    }

    /// <summary>Le csproj prive de ses lignes `<Resource>` : y etre cite compte comme un usage.</summary>
    private static string CsprojHorsDeclarations()
        => Regex.Replace(Csproj(), @"<Resource\s+Include=""[^""]+""\s*/?>", " ");

    private static bool EstUtilisee(string chemin)
    {
        var nom = Path.GetFileName(chemin);
        return CodeEtBalisage().Contains(nom) || CsprojHorsDeclarations().Contains(nom);
    }

    // Embarquees exprES, en attendant la decision. La raison n'est pas
    // decorative : c'est elle qu'on relira le jour ou l'installeur pesera trop.
    // 27/09/2026 : la decision est tombee. mascot.png et logo.png ne sont plus embarques (2 Mo de moins
    // dans l'executable et l'installeur) ; les fichiers restent dans Assets pour un ecran a venir.
    private static readonly Dictionary<string, string> GardeesSciemment = new();

    [Fact]
    public void Le_crible_lit_bien_les_ressources_du_projet()
    {
        // Temoin : sans lui, un csproj renomme rendrait tout vert sur zero ressource.
        var ressources = RessourcesEmbarquees();

        ressources.Should().Contain(@"Effets\ChromeLiquide.ps", "temoin : le shader du chrome liquide est bien lu");
        ressources.Should().Contain(@"Assets\drdxt.ico");
    }

    [Fact]
    public void Le_crible_voit_la_difference_entre_utilisee_et_orpheline()
    {
        // Temoin inverse, sur le cas exact qui separe les deux : l'icone est utilisee,
        // par `ApplicationIcon` et par `MainWindow.Icon`. Sans ce controle, un crible
        // qui declarerait tout orphelin passerait pour severe alors qu'il est casse.
        EstUtilisee(@"Assets\drdxt.ico").Should().BeTrue("l'icone est citee par ApplicationIcon et par MainWindow");
        EstUtilisee(@"Assets\inexistante-xyz.png").Should().BeFalse();
    }

    [Fact]
    public void Aucune_ressource_n_est_embarquee_sans_usage_ni_decision()
    {
        var orphelines = RessourcesEmbarquees()
            .Where(r => !EstUtilisee(r) && !GardeesSciemment.ContainsKey(r))
            .ToList();

        orphelines.Should().BeEmpty(
            "un `<Resource>` est compile DANS l'assembly : il voyage dans chaque installeur et " +
            "se charge en memoire, meme si aucun ecran ne l'affiche. Soit un ecran l'utilise, " +
            "soit il rejoint la liste des ressources gardees sciemment, avec sa raison");
    }

    [Fact]
    public void La_liste_des_ressources_gardees_sciemment_ne_vieillit_pas()
    {
        // L'autre sens. Une image qui trouve enfin son ecran, ou qu'on retire, doit
        // sortir de la liste — sinon la liste finit par decrire un produit qui n'existe
        // plus, et c'est exactement ce qu'on reproche aux documents.
        var perimees = GardeesSciemment.Keys
            .Where(r => !RessourcesEmbarquees().Contains(r) || EstUtilisee(r))
            .ToList();

        perimees.Should().BeEmpty(
            "ces ressources ne sont plus orphelines — elles ont trouve un usage ou ont ete " +
            "retirees du projet, donc elles n'ont plus rien a faire dans la liste");
    }
}
