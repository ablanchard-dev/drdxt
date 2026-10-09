using System.Globalization;
using DrDxt.App.Converters;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// 23/09/2026. Première photo de l'écran après « Optimiser » : « 13.1 ms », « 94.7 % », alors que
// l'écran du scan, juste avant, écrit « 9,5 ns ». Le convertisseur formatait en culture
// invariante ; le reste de l'app formate dans la culture de l'utilisateur. Un même produit ne
// change pas de séparateur décimal d'un écran à l'autre.
// -----------------------------------------------------------------------
public sealed class LesNombresDuTableauDeBordParlentFrancaisTests
{
    [Fact]
    public void Un_decimal_s_ecrit_avec_le_separateur_de_l_utilisateur()
    {
        var avant = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            new NumberDisplayConverter().Convert(13.1, typeof(string), "0.#", CultureInfo.InvariantCulture)
                .Should().Be("13,1");
        }
        finally { CultureInfo.CurrentCulture = avant; }
    }

    [Fact]
    public void Une_valeur_absente_reste_un_tiret()
        => new NumberDisplayConverter().Convert(null!, typeof(string), "0.#", CultureInfo.InvariantCulture)
            .Should().Be("—");
}
