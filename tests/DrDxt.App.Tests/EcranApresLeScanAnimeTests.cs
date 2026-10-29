using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

public sealed class EcranApresLeScanAnimeTests
{
    // L'écran d'après scan : ses six sections apparaissaient d'un bloc à la fin du scan.
    [Fact]
    public void Apres_le_scan_les_sections_entrent_a_leur_tour()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var scan = VueScan.Texte(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        // Le bloc « Agir » du bas (second Optimiser) est retiré ; la liste part de la mesure en jeu.
        var debut = scan.IndexOf("───── LA MESURE EN JEU ─────", StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0);

        var retards = Regex.Matches(scan[debut..], @"effets:Revelation\.Retard=""(\d+)""").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        retards.Should().HaveCount(5, "mesure en jeu, retour du BIOS, guide BIOS, bilan de santé, rapport");
        retards.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }
}
