using DrDxt.Application.Tweaks.Gpu;
using DrDxt.ElevatedHelper.Execution.NvApi;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Le helper élevé impose lui-même l'enveloppe de sécurité GPU.
//
// Le helper ne peut pas référencer DrDxt.Application (il ne dépend que de DrDxt.Core), donc
// la bande prudente est recopiée dans NvApiTuningMath. GpuSafeBounds reste la seule source ;
// ces tests échouent bruyamment si la copie dérive, et figent le rail resserré de l'exécuteur
// aux bords de la bande (le rail que traverse l'écriture directe, hors IPC, de l'outil de fumée).
// -----------------------------------------------------------------------
public sealed class NvApiTuningPolicyBoundSyncTests
{
    // --- La copie doit égaler GpuSafeBounds, feuille par feuille. ---

    [Fact]
    public void Core_offset_band_mirrors_GpuSafeBounds()
    {
        NvApiTuningMath.MinCoreOffsetMhz.Should().Be(GpuSafeBounds.MinCoreOffsetMhz);
        NvApiTuningMath.MaxCoreOffsetMhz.Should().Be(GpuSafeBounds.MaxCoreOffsetMhz);
    }

    [Fact]
    public void Memory_offset_band_mirrors_GpuSafeBounds()
    {
        NvApiTuningMath.MinMemoryOffsetMhz.Should().Be(GpuSafeBounds.MinMemoryOffsetMhz);
        NvApiTuningMath.MaxMemoryOffsetMhz.Should().Be(GpuSafeBounds.MaxMemoryOffsetMhz);
    }

    // --- Le rail resserré de l'exécuteur impose la même bande (c'est le chemin d'un appel direct,
    //     hors IPC, à NvApiTuningActions.SetCoreClockOffset depuis l'outil de fumée). ---

    [Theory]
    [InlineData(100, true)]      // borne haute : dans la bande
    [InlineData(-200, true)]     // borne basse : dans la bande
    [InlineData(0, true)]
    [InlineData(101, false)]     // un cran au-dessus
    [InlineData(-201, false)]    // un cran en dessous
    [InlineData(500, false)]     // l'ancien rail ±500, désormais refusé
    [InlineData(int.MinValue, false)]  // pas de débordement Math.Abs
    [InlineData(int.MaxValue, false)]
    public void Core_offset_rail_enforces_the_policy_band(int mhz, bool inBand) =>
        NvApiTuningMath.IsCoreOffsetWithinHardRail(mhz).Should().Be(inBand);

    [Theory]
    [InlineData(500, true)]
    [InlineData(-500, true)]
    [InlineData(0, true)]
    [InlineData(501, false)]
    [InlineData(-501, false)]
    [InlineData(int.MinValue, false)]
    public void Memory_offset_rail_enforces_the_policy_band(int mhz, bool inBand) =>
        NvApiTuningMath.IsMemoryOffsetWithinHardRail(mhz).Should().Be(inBand);
}
