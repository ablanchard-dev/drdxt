using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 03/10/2026 (boucle 2D2P, D11.5, revue n° 5) : « Remesurer · 40 s » pesait autant que « Rendre à Windows ». Les gestes
// qui mesurent ou testent ont la capsule sombre cerclée de cyan ; le chrome plein reste à ceux qui changent la machine.
public sealed class MesurerNePesePasCommeCorrigerTests
{
    private static string Vue()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
    }

    private static string StyleDe(string commande)
    {
        var m = Regex.Match(Vue(), @"Style=""\{StaticResource (\w+)\}""[^<]*Command=""\{Binding " + commande + @"\}""");
        m.Success.Should().BeTrue($"témoin : {commande} est une capsule à maintenir");
        return m.Groups[1].Value;
    }

    [Theory]
    [InlineData("RemesurerProcesseurCommand")]
    [InlineData("LancerTestMemoireCommand")]
    [InlineData("RemesurerMemoireCommand")]
    public void Mesurer_ou_tester_a_la_capsule_de_mesure(string commande) => StyleDe(commande).Should().Be("CapsuleDeMesure");

    [Theory]
    [InlineData("RendreLeReposALaCarteCommand")]
    [InlineData("RendreBcdCommand")]
    [InlineData("LancerReparationCommand")]
    public void Corriger_garde_le_chrome_plein(string commande) => StyleDe(commande).Should().Be("CapsuleDeMaintien");
}
