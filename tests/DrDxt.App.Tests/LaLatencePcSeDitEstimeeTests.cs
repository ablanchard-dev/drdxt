using System.Globalization;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;
using Xunit;

namespace DrDxt.App.Tests;

// La latence PC de PresentMon est une estimation de la part du PC, pas le vrai clic → photon (il faut un capteur) :
// le cahier des charges rend le mot obligatoire à l'écran, dans les deux langues du produit.
public sealed class LaLatencePcSeDitEstimeeTests
{
    [Theory]
    [InlineData("fr-FR", "estimée")]
    [InlineData("en-US", "estimated")]
    public void La_latence_pc_affichee_se_dit_estimee(string langue, string mot)
        => new ResxLocalizer().Get("Parties.LatencePc", CultureInfo.GetCultureInfo(langue)).Should().Contain(mot);
}
