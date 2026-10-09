using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, D11.2, revue n° 5) : dans « Agir » du Processeur, le bloc « programmes gourmands » passait
// en titre, texte, bouton dessous, sous une action en grille bouton à gauche / texte à droite. Même grille pour les deux.
public sealed class AgirSurUneGrilleTests
{
    [Fact]
    public void Le_bloc_programmes_gourmands_suit_la_grille_bouton_texte()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var grille = Regex.Match(xaml, "<Grid x:Name=\"GrilleCalmer\"(.*?)</Grid>", RegexOptions.Singleline).Groups[1].Value;
        grille.Should().NotBeEmpty("témoin : la grille existe");
        grille.Should().Contain("<ColumnDefinition Width=\"Auto\"/>").And.Contain("<StackPanel Grid.Column=\"2\"");
        grille.IndexOf("BoutonCalmerVoisins", StringComparison.Ordinal)
            .Should().BeLessThan(grille.IndexOf("Grid.Column=\"2\"", StringComparison.Ordinal), "le bouton d'abord, à gauche");
    }

    // 03/10/2026 (D12.3, revue n° 6) : même grille pour la veille du ping, dans Agir Réseau.
    [Fact]
    public void La_veille_du_ping_suit_la_grille_bouton_texte()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var grille = Regex.Match(xaml, "<Grid x:Name=\"GrilleVeille\"(.*?)</Grid>", RegexOptions.Singleline).Groups[1].Value;
        grille.Should().NotBeEmpty("témoin : la grille existe");
        grille.IndexOf("BasculerVeillePingCommand", StringComparison.Ordinal)
            .Should().BeLessThan(grille.IndexOf("Grid.Column=\"2\"", StringComparison.Ordinal), "le bouton d'abord, à gauche");
    }
}
