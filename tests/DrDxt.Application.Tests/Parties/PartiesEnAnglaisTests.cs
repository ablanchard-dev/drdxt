using System.Globalization;
using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Les constats tirés des parties suivent la langue de l'App, comme ceux du docteur.
public sealed class PartiesEnAnglaisTests
{
    [Fact]
    public void Stabilite_et_refroidissement_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));
        var maintenant = DateTimeOffset.Now;

        StabiliteApresChangement.NonLu([new Changement(maintenant.AddDays(-1), "HAGS")], maintenant).Single().Explication
            .Should().StartWith("the shutdown log or the hardware error log could not be read");
        TestDeRefroidissement.Juger(new TestDeRefroidissement.Resultat(30, null, false, null, LectureePerdue: true), null).Explication
            .Should().StartWith("the processor state stopped being read");
    }
}
