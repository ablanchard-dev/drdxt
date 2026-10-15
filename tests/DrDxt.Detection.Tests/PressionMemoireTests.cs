using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests;

/// <summary>La mémoire se lit sur la vraie machine qui lance la suite, sans droits et sans écriture.
/// C'est la preuve « lue sur un vrai PC » demandée par la ligne.</summary>
public sealed class PressionMemoireTests
{
    [Fact]
    public void La_memoire_de_cette_machine_se_lit_et_tient_debout()
    {
        var lu = PressionMemoire.Lire();

        lu.Should().NotBeNull("GlobalMemoryStatusEx ne demande aucun droit");
        lu!.TotalOctets.Should().BeGreaterThan(1UL << 30, "une machine qui lance la suite a plus d'un Go");
        lu.DisponiblesOctets.Should().BeLessThanOrEqualTo(lu.TotalOctets);
        lu.EngagementOctets.Should().BeLessThanOrEqualTo(lu.LimiteEngagementOctets);
        lu.LimiteEngagementOctets.Should().BeGreaterThanOrEqualTo(lu.TotalOctets / 2, "la limite d'engagement compte au moins une bonne part de la mémoire");
    }
}
