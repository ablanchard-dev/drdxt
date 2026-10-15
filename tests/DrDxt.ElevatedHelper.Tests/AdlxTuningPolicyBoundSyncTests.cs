using DrDxt.Application.Tweaks.Gpu;
using DrDxt.ElevatedHelper.Execution.Adlx;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Le helper élevé impose lui-même l'enveloppe de sécurité GPU pour la couture
// d'écriture ADLX (AMD), miroir de NvApiTuningPolicyBoundSyncTests.
//
// Le helper ne peut pas référencer DrDxt.Application (il ne dépend que de DrDxt.Core et de la
// feuille DrDxt.GpuNative), donc la bande prudente de limite de puissance est recopiée dans
// AdlxTuningMath. GpuSafeBounds reste la seule source ; ces tests échouent bruyamment si la
// copie dérive, et figent le rail côté helper (le refus par défaut que traverse chaque
// écriture de limite de puissance ADLX) aux bords de la bande.
//
// L'op Undervolt porte une MaxFreq absolue (MHz), pas un décalage : il n'y a donc pas de
// bande de décalage à recopier ici. La bande prudente [-200, +100] est imposée côté App
// (GpuSafeBounds) et le helper valide la cible absolue contre la plage matérielle annoncée
// par le pilote (vérifié dans ADLXTuningActionsTests, pas ici).
// -----------------------------------------------------------------------
public sealed class AdlxTuningPolicyBoundSyncTests
{
    // --- La copie doit égaler GpuSafeBounds, feuille par feuille. ---

    [Fact]
    public void Power_limit_band_mirrors_GpuSafeBounds()
    {
        AdlxTuningMath.MinPowerLimitPct.Should().Be(GpuSafeBounds.MinPowerLimitPct);
        AdlxTuningMath.MaxPowerLimitPct.Should().Be(GpuSafeBounds.MaxPowerLimitPct);
    }

    // --- Le rail côté helper impose la même bande (le refus par défaut). ---

    [Theory]
    [InlineData(60, true)]             // plancher : dans la bande
    [InlineData(100, true)]            // plafond : dans la bande
    [InlineData(80, true)]
    [InlineData(59, false)]            // sous le plancher
    [InlineData(101, false)]           // au-dessus du plafond
    [InlineData(int.MinValue, false)]
    [InlineData(int.MaxValue, false)]
    public void Power_limit_rail_enforces_the_policy_band(int pct, bool inBand) =>
        AdlxTuningMath.IsPowerLimitWithinHardRail(pct).Should().Be(inBand);
}
