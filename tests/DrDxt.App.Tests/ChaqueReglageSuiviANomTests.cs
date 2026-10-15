using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Chaque réglage suivi se nomme par « Reglage.<id> » dans le journal et la frise ; une clé absente s'affichait brute
// (« Reglage.bios.courbe_ventilateur_cpu »). Les identifiants sont relus dans le code qui les note.
public sealed class ChaqueReglageSuiviANomTests
{
    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Chaque_reglage_suivi_a_son_nom(string langue)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var code = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "ViewModels", "ScanViewModel.Parties.cs"));
        var ids = Regex.Matches(code, @"""((?:bios|memoire|nvidia|windows)\.[a-z_]+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        ids.Should().Contain("bios.courbe_ventilateur_cpu", "témoin : le crible lit bien les identifiants");

        var loc = new ResxLocalizer();
        ids.Where(id => loc.Get("Reglage." + id, new CultureInfo(langue)) == "Reglage." + id)
            .Should().BeEmpty("un réglage suivi sans nom s'affiche en clé brute");
    }
}
