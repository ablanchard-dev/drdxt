using System.Globalization;
using System.IO;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

/// <summary>
/// Sous « Rendre le repos » et « Remesurer le processeur », trois à cinq lignes grises.
/// Une ligne reste à l'écran ; le texte entier passe en info-bulle sur cette ligne, rien n'est perdu. Les réparations de
/// Windows gardent leur texte entier : il porte un coût à lire avant le geste.
/// </summary>
public sealed class AgirEnUneLigneTests
{
    [Theory]
    [InlineData("Carte.Repos.Description")]
    [InlineData("Cpu.Remesure.Description")]
    public void La_ligne_courte_tient_sur_une_ligne_et_le_detail_reste_en_info_bulle(string cle)
    {
        foreach (var culture in new[] { "fr", "en" })
        {
            var court = new ResxLocalizer().Get(cle + ".Court", new CultureInfo(culture));
            court.Should().NotBe(cle + ".Court", "la clé existe en " + culture);
            court.Length.Should().BeLessThan(100, culture);
        }

        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = VueScan.Texte(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        var propriete = cle == "Carte.Repos.Description" ? "LabelReposCarteDescription" : "LabelRemesureCpuDescription";
        xaml.Should().Contain($"Text=\"{{Binding {propriete}Courte}}\"").And.Contain($"ToolTip=\"{{Binding {propriete}}}\"");
    }
}
