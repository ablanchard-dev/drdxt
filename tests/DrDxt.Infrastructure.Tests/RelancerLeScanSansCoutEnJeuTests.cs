using System.Globalization;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests;

/// <summary>
/// « Relance le scan en jeu » pousserait a scanner pendant une partie, donc a charger la
/// machine pendant qu'on joue (mesure : une charge de fond coute 40 % d'images). Les reglages
/// qui dependent du jeu ont seulement besoin que le processus du jeu tourne : le menu suffit.
/// </summary>
public sealed class RelancerLeScanSansCoutEnJeuTests
{
    [Theory]
    [InlineData("fr", "Relance le scan en jeu", "le menu suffit")]
    [InlineData("en", "Scan again in game", "the menu is enough")]
    public void Le_conseil_ne_pousse_pas_a_scanner_en_pleine_partie(string langue, string ancien, string juste)
    {
        var texte = new ResxLocalizer().Get("Scan.Recommendations.None", new CultureInfo(langue));

        texte.Should().NotContain(ancien).And.Contain(juste);
    }
}
