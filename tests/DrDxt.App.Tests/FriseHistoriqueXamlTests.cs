using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// La frise de l'historique est bornée comme les autres listes à lire. Les FPS d'une partie, en police fixe, passent
// sous le nom du jeu : rangés dans une colonne à droite, ils prenaient toute la largeur, le nom se repliait lettre
// par lettre en une longue colonne vide et les chiffres sortaient coupés à droite.
public sealed class FriseHistoriqueXamlTests
{
    private static string Frise()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var vue = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var debut = vue.IndexOf("ItemsSource=\"{Binding FriseHistorique}\"", StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0, "témoin : la frise est trouvée");
        return vue[debut..vue.IndexOf("</ItemsControl>", debut, StringComparison.Ordinal)];
    }

    [Fact]
    public void La_frise_garde_une_largeur_de_lecture()
    {
        var frise = Frise();
        frise[..frise.IndexOf('>')].Should().Contain("MaxWidth=").And.Contain("HorizontalAlignment=\"Left\"");
    }

    [Fact]
    public void Les_fps_passent_sous_le_texte_et_se_replient()
    {
        var frise = Frise();
        frise.Should().NotContain("Grid.Column=\"3\"", "aucune colonne à droite ne vole la largeur du texte");
        var detail = frise[frise.IndexOf("{Binding Detail}", StringComparison.Ordinal)..];
        detail[..detail.IndexOf("/>", StringComparison.Ordinal)].Should().Contain("TextWrapping=\"Wrap\"");
    }
}
