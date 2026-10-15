using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// La page Processeur empilait cinq blocs, et ce qui
// bride le processeur en jeu arrivait après toutes les listes. Il vient maintenant tout de suite après le verdict.
public sealed class PageProcesseurAllegeeTests
{
    [Fact]
    public void Ce_qui_bride_le_processeur_vient_avant_les_mesures()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));

        var verdict = xaml.IndexOf("Text=\"{Binding Verdict}\"", StringComparison.Ordinal);
        var bride = xaml.IndexOf("LabelLimiteTitre", StringComparison.Ordinal);
        var mesures = xaml.IndexOf("LabelCarteMesures", StringComparison.Ordinal);

        new[] { verdict, bride, mesures }.Should().OnlyContain(i => i > 0, "témoin : les trois repères sont dans la vue");
        bride.Should().BeGreaterThan(verdict).And.BeLessThan(mesures);
    }

    // Quatre paragraphes avant la première mesure. La première phrase
    // reste à l'écran ; le réglage, le risque et le retour arrière se déplient sous « Pourquoi ? ».
    [Fact]
    public void La_bride_se_dit_en_une_phrase_et_le_reste_se_deplie()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));

        xaml.Should().NotContain("ItemsSource=\"{Binding DataContext.LignesLimiteDePuissance",
            "les quatre paragraphes ne s'affichent plus d'un bloc");
        xaml.Should().Contain("Text=\"{Binding DataContext.LimitePremiereLigne");
        xaml.Should().MatchRegex("ItemsSource=\"\\{Binding DataContext\\.LignesLimiteSuite[^>]*Visibility=\"\\{Binding IsChecked, ElementName=PourquoiBride");
    }
}
