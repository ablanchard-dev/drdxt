using DrDxt.Detection.Settings;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Settings;

public class PowerProviderTests
{
    [Fact]
    public void Active_scheme_guid_is_returned_without_throwing()
    {
        var sut = new PowerProvider();
        var act = () => sut.GetActiveSchemeGuid();
        var r = act.Should().NotThrow().Subject;
        r.IsOk.Should().BeTrue();
        Guid.TryParse(r.Value, out _).Should().BeTrue();
    }
}
