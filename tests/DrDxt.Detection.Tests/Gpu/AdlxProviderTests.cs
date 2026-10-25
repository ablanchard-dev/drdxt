using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Gpu;

public class AdlxProviderTests
{
    [Fact]
    public void Rebar_query_never_throws_and_is_unknown_when_adlx_absent()
    {
        // ADLX absent (pas de carte AMD, pas de pilote Adrenalin) : Unknown, jamais Off inventé, et rien ne lève.
        new AdlxProvider(() => DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus.RienLu("absent")).GetSmartAccessMemoryState()
            .Should().Be(TriState.Unknown);
        var act = () => new AdlxProvider(() => throw new InvalidOperationException()).GetSmartAccessMemoryState();
        act.Should().NotThrow().Subject.Should().Be(TriState.Unknown);
    }
}
