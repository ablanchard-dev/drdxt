using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>29/09/2026, troisième critique : en lisant un long domaine, le rail des domaines partait avec la page
/// et laissait une colonne vide avec un fil seul. Il suit la lecture sans jamais sortir de sa section.</summary>
public sealed class RailCollantTests
{
    [Theory]
    [InlineData(100, 2000, 500, 0)]     // section encore sous le haut de la vue : le rail reste à sa place
    [InlineData(-300, 2000, 500, 316)]  // section entamée de 300 : le rail descend d'autant, plus la marge
    [InlineData(-1900, 2000, 500, 1500)] // fin de section : le rail s'arrête à son bas, il ne déborde pas
    [InlineData(-300, 400, 500, 0)]     // section plus courte que le rail : rien à faire
    public void Le_rail_suit_la_lecture_sans_sortir_de_sa_section(double hautDeSection, double hauteurDeSection, double hauteurDuRail, double attendu)
    {
        RailCollant.Decalage(hautDeSection, hauteurDeSection, hauteurDuRail, marge: 16).Should().Be(attendu);
    }
}
