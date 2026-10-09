using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>28/09/2026 : l'écran avant/après entre en scène. Les cartes arrivent dans l'ordre de lecture, les
/// barres des lows montent depuis leur base ; les chiffres, eux, sont les vrais dès la première image.</summary>
public sealed class EcranAvantApresAnimeTests
{
    private static string Vue()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "DashboardView.xaml"));
    }

    [Fact]
    public void Chaque_carte_entre_a_son_tour_dans_l_ordre_de_lecture()
    {
        var retards = Regex.Matches(Vue(), @"Style=""\{StaticResource Dalle\}""[^>]*effets:Revelation\.Retard=""(\d+)""")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();

        retards.Should().HaveCount(5, "les cinq cartes de l'écran");
        retards.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Les_quatre_barres_montent_et_aucun_chiffre_n_est_anime()
    {
        var vue = Vue();
        Regex.Matches(vue, @"<Rectangle Width=""38""[^>]*effets:Croissance\.Active=""True""").Count.Should().Be(4);
        vue.Should().NotContain("Storyboard.TargetProperty=\"Text\"", "un chiffre affiché est toujours le vrai");
    }

    // L'écran d'après scan : ses six sections apparaissaient d'un bloc à la fin du scan.
    [Fact]
    public void Apres_le_scan_les_sections_entrent_a_leur_tour()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var scan = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        // 29/09/2026 : le bloc « Agir » du bas (second Optimiser) est retiré ; la liste part de la mesure en jeu.
        var debut = scan.IndexOf("───── LA MESURE EN JEU ─────", StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0);

        var retards = Regex.Matches(scan[debut..], @"effets:Revelation\.Retard=""(\d+)""").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        retards.Should().HaveCount(5, "mesure en jeu, retour du BIOS, guide BIOS, bilan de santé, rapport");
        retards.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Les_deux_barres_de_latence_s_etirent_depuis_la_gauche()
        => Regex.Matches(Vue(), @"<Border Height=""12""[^>]*effets:Croissance\.Active=""True"" effets:Croissance\.Horizontale=""True""")
            .Count.Should().Be(2);
}
