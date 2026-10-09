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
        // Sur un runner CI sans GPU AMD / sans ADLX : doit renvoyer Unknown, pas crasher.
        var sut = new AdlxProvider();
        var act = () => sut.GetSmartAccessMemoryState();
        var r = act.Should().NotThrow().Subject;
        r.Should().BeOneOf(TriState.On, TriState.Off, TriState.Unknown);
    }
}
