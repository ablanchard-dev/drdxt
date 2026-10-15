using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Chaque couleur de texte garde un contraste d'au moins 4,5:1 (WCAG AA, texte courant) sur chaque fond d'obsidienne.
/// Le gris le plus discret est le plus près de la limite : 4,7:1 sur le fond relevé. Un gris éclairci ou un fond assombri
/// qui passerait dessous échoue ici avant d'arriver à l'écran.
/// </summary>
public sealed class LesGrisDeTexteRestentLisiblesTests
{
    private static Dictionary<string, string> Couleurs()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent!;
        var xaml = File.ReadAllText(Path.Combine(d.FullName, "src", "DrDxt.App", "App.xaml"));
        return Regex.Matches(xaml, @"<Color x:Key=""(\w+)Color"">#FF([0-9A-Fa-f]{6})</Color>")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static double Luminance(string hex)
    {
        double Canal(int i)
        {
            var c = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Canal(0) + 0.7152 * Canal(2) + 0.0722 * Canal(4);
    }

    private static double Contraste(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [InlineData("Texte")]
    [InlineData("TexteSecondaire")]
    [InlineData("TexteDiscret")]
    public void La_couleur_de_texte_tient_4_5_sur_chaque_fond(string texte)
    {
        var c = Couleurs();
        c.Should().ContainKey(texte, "témoin : la couleur est bien lue dans App.xaml");
        foreach (var fond in new[] { "Obsidienne", "ObsidienneHaute", "ObsidienneRelevee" })
            Contraste(c[texte], c[fond]).Should().BeGreaterThanOrEqualTo(4.5, $"{texte} sur {fond}");
    }
}
