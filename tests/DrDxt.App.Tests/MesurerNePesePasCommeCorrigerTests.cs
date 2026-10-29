using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.App.Tests;

// Trois poids de bouton. Le chrome plein n'appartient qu'à l'action principale de l'écran, Optimiser (DESIGN.md, règle 5).
// Les gestes qui changent la machine ont la capsule sombre au liseré chrome ; ceux qui mesurent ou testent, cerclée de cyan.
public sealed class MesurerNePesePasCommeCorrigerTests
{
    private static string Vue()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return VueScan.Texte(Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
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
    public void Corriger_a_la_capsule_de_correction(string commande) => StyleDe(commande).Should().Be("CapsuleDeCorrection");

    [Fact]
    public void Un_seul_chrome_plein_sur_l_ecran_et_c_est_optimiser()
    {
        var vue = Vue();
        Regex.Matches(vue, @"StaticResource CapsuleChrome\b").Count.Should().Be(1, "une seule action principale");
        Regex.IsMatch(vue, @"x:Name=""BoutonOptimiser"" Style=""\WStaticResource CapsuleChrome\W").Should().BeTrue("et c'est Optimiser");
        Regex.IsMatch(vue, @"StaticResource CapsuleDeMaintien\b").Should().BeFalse("la capsule de maintien en chrome plein ferait une seconde action principale");
    }
}
