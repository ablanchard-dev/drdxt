using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

// À la fin du scan, verdict, aperçu, Optimiser, liste et domaines apparaissaient d'un bloc. Ils entrent dans l'ordre de
// lecture, chacun après le précédent. Optimiser vient avant la liste des défauts : c'est l'appel principal de l'écran.
public sealed class ResultatsEnCascadeTests
{
    private static readonly string[] OrdreDeLecture = ["ApercuOptimiser", "BoutonOptimiser", "ListeAFaire", "CarteMachine"];

    [Fact]
    public void Les_resultats_entrent_dans_l_ordre_de_lecture()
    {
        var xaml = VueScan.Texte(Path.Combine(Racine(), "src", "DrDxt.App", "Views", "ScanView.xaml"));

        var retards = OrdreDeLecture.Select(nom =>
        {
            var balise = Regex.Match(xaml, $"<[^<>]*x:Name=\"{nom}\"[^<>]*>").Value;
            balise.Should().NotBeEmpty($"l'élément {nom} existe dans la vue");
            balise.Should().Contain("effets:Revelation.Active=\"True\"", $"{nom} entre en scène");
            return int.Parse(Regex.Match(balise, "effets:Revelation.Retard=\"(\\d+)\"").Groups[1].Value);
        }).ToList();

        retards.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        retards[0].Should().BePositive("le verdict entre d'abord, Optimiser après lui");
    }

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
