using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Un chiffre publié qui s'affiche porte son adresse, sinon il n'est pas affiché (CONTRIBUTING). Ces constats citaient un
// coût sans source : le chiffre d'Instant Replay n'a pas d'adresse NVIDIA actuelle, ceux de XMP pas de test TechSpot ou XDA
// retrouvé, et celui de l'intégrité de la mémoire nommait un autre processeur que celui de la mesure.
public class UnChiffrePublieASonAdresseTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    private static string Valeur(string fichier, string cle) => XDocument.Load(Path.Combine(Racine(), fichier)).Root!
        .Elements("data").Single(x => x.Attribute("name")!.Value == cle).Element("value")!.Value;

    [Theory]
    [InlineData(@"src\DrDxt.Core\Resources\Docteur.fr.resx", "EN.Valeur")]
    [InlineData(@"src\DrDxt.Core\Resources\Docteur.resx", "EN.Valeur")]
    [InlineData(@"src\DrDxt.Core\Resources\Docteur.fr.resx", "EN.Explication")]
    [InlineData(@"src\DrDxt.Core\Resources\Docteur.resx", "EN.Explication")]
    [InlineData(@"src\DrDxt.Core\Resources\Docteur.fr.resx", "IH.ActiveExplication")]
    [InlineData(@"src\DrDxt.Core\Resources\Docteur.resx", "IH.ActiveExplication")]
    [InlineData(@"src\DrDxt.Infrastructure\Resources\Strings.fr.resx", "Bios.Gain.Xmp.Caveat")]
    [InlineData(@"src\DrDxt.Infrastructure\Resources\Strings.resx", "Bios.Gain.Xmp.Caveat")]
    [InlineData(@"src\DrDxt.Infrastructure\Resources\Strings.fr.resx", "tweak.fixit.cpu_power_limit.summary")]
    [InlineData(@"src\DrDxt.Infrastructure\Resources\Strings.resx", "tweak.fixit.cpu_power_limit.summary")]
    public void Un_cout_publie_porte_son_adresse_ou_ne_s_affiche_pas(string fichier, string cle)
    {
        var v = Valeur(fichier, cle);

        if (Regex.IsMatch(v, @"\d\s?%")) v.Should().Contain("https://", $"{cle} cite un chiffre");
    }

    // La mesure de ComputerBase porte sur un Ryzen 7 5800X ; son 8 % est celui du 1 % bas, 6 % celui des images moyennes.
    [Theory]
    [InlineData(@"src\DrDxt.Core\Resources\Docteur.fr.resx")]
    [InlineData(@"src\DrDxt.Core\Resources\Docteur.resx")]
    public void L_integrite_de_la_memoire_nomme_le_processeur_de_la_mesure(string fichier)
        => Valeur(fichier, "IH.ActiveExplication").Should().Contain("5800X").And.NotContain("5800X3D");
}
