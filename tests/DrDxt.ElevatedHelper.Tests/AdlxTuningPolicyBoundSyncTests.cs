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
// L'op Undervolt porte une MaxFreq absolue (MHz) : la bande prudente [-200, +100] est recopiée
// aussi, et le helper l'applique autour de la MaxFreq retenue à ce démarrage (vérifié dans
// ADLXTuningActionsTests).
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

    [Fact]
    public void Undervolt_band_mirrors_GpuSafeBounds()
    {
        AdlxTuningMath.MinCoreOffsetMhz.Should().Be(GpuSafeBounds.MinCoreOffsetMhz);
        AdlxTuningMath.MaxCoreOffsetMhz.Should().Be(GpuSafeBounds.MaxCoreOffsetMhz);
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
    // La même bande dite en décalage autour de l'usine, la forme de la limite de Radeon Software (0 = usine) : jamais
    // au-dessus de l'usine, jamais sous le plancher.
    [InlineData(0, true)]
    [InlineData(-10, true)]
    [InlineData(-40, true)]            // plancher : 100 - 40 = 60 %
    [InlineData(-41, false)]
    [InlineData(1, false)]             // au-dessus de l'usine
    [InlineData(15, false)]
    public void Power_limit_rail_enforces_the_policy_band(int pct, bool inBand) =>
        AdlxTuningMath.IsPowerLimitWithinHardRail(pct).Should().Be(inBand);

    [Fact]
    public void Power_limit_offset_band_mirrors_GpuSafeBounds()
    {
        AdlxTuningMath.MinPowerLimitOffset.Should().Be(GpuSafeBounds.MinPowerLimitPct - 100);
        AdlxTuningMath.MaxPowerLimitOffset.Should().Be(GpuSafeBounds.MaxPowerLimitPct - 100);
    }
}
