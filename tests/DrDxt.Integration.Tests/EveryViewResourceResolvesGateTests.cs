using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Une ressource XAML introuvable ne se voit qu'à l'affichage, et elle tue l'écran.
///
/// WPF résout une `{StaticResource}` par l'ARBRE VISUEL : un style déclaré dans une vue n'existe pas
/// pour sa voisine. Rien dans la compilation ne le dit, rien dans les tests unitaires non plus : le
/// modèle de vue est testable sans jamais construire la vue. Le défaut n'apparaît qu'à l'affichage
/// (par exemple `TextBrush` au lieu de `TextPrimaryBrush`, ou un style déclaré chez le voisin).
///
/// Ce gate lit chaque vue et exige que chaque `{StaticResource}` soit déclarée SOIT au niveau
/// application (`App.xaml`), SOIT dans la vue elle-même. C'est exactement la portée que WPF
/// accorde à un `UserControl` monté seul : grouper tous les fichiers laisserait passer une ressource
/// déclarée « quelque part » ailleurs.
/// </summary>
public sealed class EveryViewResourceResolvesGateTests
{
    private static readonly Regex Declared = new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex Used = new(@"\{StaticResource ([A-Za-z0-9_.]+)", RegexOptions.Compiled);

    [Fact]
    public void Every_StaticResource_a_view_uses_is_visible_from_that_view()
    {
        var root = RepoRoot();
        var appLevel = KeysIn(Path.Combine(root, "src", "DrDxt.App", "App.xaml"));
        appLevel.Should().NotBeEmpty("App.xaml doit porter le dictionnaire applicatif, sinon ce gate est vert pour rien");

        // Tout le XAML de l'application, pas seulement le dossier Views. Mesure du 05/09/2026 :
        // ce gate enumerait `Views/*.xaml` et laissait `MainWindow.xaml` dehors — la COQUILLE qui
        // heberge les vues. Une ressource introuvable la-bas ne tue pas un ecran : elle tue le
        // demarrage, avant que l'utilisateur ait pu cliquer quoi que ce soit. Le fichier le plus
        // exposé était le seul non surveillé.
        // App.xaml est exclu : c'est le dictionnaire de reference, pas un consommateur.
        var appXamlPath = Path.Combine(root, "src", "DrDxt.App", "App.xaml");
        var views = FichiersDuDepot.Sous(Path.Combine(root, "src", "DrDxt.App"), "*.xaml")
            .Where(f => !string.Equals(f, appXamlPath, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        views.Should().NotBeEmpty("le scan doit trouver des vues");
        views.Select(Path.GetFileName).Should().Contain("MainWindow.xaml",
            "la coquille est le fichier dont l'echec coute le plus cher : il empeche le demarrage");

        var offenders = new List<string>();
        foreach (var view in views)
        {
            var text = File.ReadAllText(view);
            // La portée réelle d'un UserControl : le dictionnaire applicatif + le sien. Grouper
            // toutes les vues ensemble est precisement l'erreur qui a laisse passer le crash.
            var visible = appLevel.Union(Declared.Matches(text).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);

            var missing = Used.Matches(text)
                .Select(m => m.Groups[1].Value)
                .Where(k => !visible.Contains(k))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            if (missing.Count > 0)
                offenders.Add($"{Path.GetFileName(view)} : {string.Join(", ", missing)}");
        }

        offenders.Should().BeEmpty(
            "une ressource introuvable ne casse rien a la compilation et tue l'ecran a l'affichage");
    }

    [Fact]
    public void The_gate_would_actually_catch_a_resource_borrowed_from_another_view()
    {
        // Controle POSITIF : sans lui, une expression reguliere cassee donnerait la meme couleur
        // qu'un depot sain — et c'est exactement ce qui est arrive a la premiere version.
        var appLevel = new HashSet<string>(StringComparer.Ordinal) { "TextPrimaryBrush" };
        const string view = """
            <UserControl>
              <UserControl.Resources><Style x:Key="Local"/></UserControl.Resources>
              <TextBlock Style="{StaticResource Local}" Foreground="{StaticResource TextPrimaryBrush}"/>
              <TextBlock Style="{StaticResource DeclareChezLeVoisin}"/>
            </UserControl>
            """;

        var visible = appLevel.Union(Declared.Matches(view).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        var missing = Used.Matches(view).Select(m => m.Groups[1].Value).Where(k => !visible.Contains(k)).ToList();

        missing.Should().ContainSingle().Which.Should().Be("DeclareChezLeVoisin");
    }

    private static HashSet<string> KeysIn(string path) =>
        Declared.Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }
}
