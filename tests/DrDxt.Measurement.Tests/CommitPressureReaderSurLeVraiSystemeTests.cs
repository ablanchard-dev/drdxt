using FluentAssertions;

namespace DrDxt.Measurement.Tests;

// 26/09/2026 : lu sur le vrai système (GlobalMemoryStatusEx), jamais exercé par un test jusqu'ici. La
// pression de mémoire engagée est une fraction : strictement positive (ce processus en engage), au plus 1.
public sealed class CommitPressureReaderSurLeVraiSystemeTests
{
    [Fact]
    public void La_pression_lue_est_une_fraction_reelle()
    {
        var p = CommitPressureReader.Read();

        p.Should().NotBeNull("GlobalMemoryStatusEx répond sur toute machine Windows");
        p!.Value.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1);
    }
}
