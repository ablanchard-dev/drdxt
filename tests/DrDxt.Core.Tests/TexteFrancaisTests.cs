using System.Globalization;
using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le runner de la CI tourne en anglais, la machine de développement en français : un test qui ne
/// fixe pas la culture ne voit ce défaut que d'un côté. Ici, la culture anglaise est imposée.
/// </summary>
public sealed class TexteFrancaisTests
{
    [Fact]
    public void Une_phrase_francaise_garde_la_virgule_sous_un_Windows_anglais()
    {
        var avant = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var duree = 10.7;
            TexteFrancais.F($"{duree:0.0} s").Should().Be("10,7 s");
            $"{duree:0.0} s".Should().Be("10.7 s", "témoin : sans F, la culture anglaise gagne");
        }
        finally
        {
            CultureInfo.CurrentCulture = avant;
        }
    }

    // La barre d'une date « dd/MM/yyyy » est le séparateur de la culture : un Windows allemand écrit
    // « 26.09.2026 ». La culture anglaise ne le voit pas (elle écrit aussi « / »), d'où l'allemand ici.
    [Fact]
    public void Une_date_francaise_garde_ses_barres_sous_un_Windows_allemand()
    {
        var avant = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var jour = new DateTime(2026, 9, 26, 7, 5, 0);
            TexteFrancais.F($"le {jour:dd/MM/yyyy HH:mm}").Should().Be("le 26/09/2026 07:05");
            $"{jour:dd/MM/yyyy}".Should().Be("26.09.2026", "témoin : sans F, la culture allemande gagne");
        }
        finally
        {
            CultureInfo.CurrentCulture = avant;
        }
    }
}
