using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// L'arithmétique mémoire, vérifiée contre des repères PUBLICS et vérifiables — pas contre
/// elle-même. Un test qui ne fait que réexécuter la formule qu'il teste ne prouve rien.
/// </summary>
public class MemoryMathTests
{
    [Theory]
    // Les trois repères classiques du domaine. Ils sont l'oracle : si la formule s'en écarte,
    // c'est la formule qui a tort.
    [InlineData(16, 3200, 10.00)]   // DDR4-3200 CL16 — la référence historique
    [InlineData(16, 3600, 8.89)]    // DDR4-3600 CL16
    [InlineData(30, 6000, 10.00)]   // DDR5-6000 CL30 — identique au DDR4-3200 CL16
    public void True_latency_matches_the_published_reference_points(int cl, int mts, double expectedNs)
    {
        MemoryMath.TrueLatencyNs(cl, mts).Should().BeApproximately(expectedNs, 0.01);
    }

    [Fact]
    public void Cycle_time_follows_the_double_data_rate()
    {
        // DDR4-3800 = 1900 MHz réels => tCK = 0,5263 ns.
        MemoryMath.CycleTimeNs(3800).Should().BeApproximately(0.5263, 0.0001);
        MemoryMath.CycleTimeNs(4000).Should().BeApproximately(0.5000, 0.0001);
    }

    [Theory]
    [InlineData(0, 3200)]
    [InlineData(16, 0)]
    [InlineData(-1, 3200)]
    public void An_unread_input_yields_zero_and_never_a_fabricated_number(int cl, int mts)
    {
        // 0 = « pas calculable », jamais « 0 ns ». L'appelant doit pouvoir distinguer.
        MemoryMath.TrueLatencyNs(cl, mts).Should().Be(0);
    }
}
