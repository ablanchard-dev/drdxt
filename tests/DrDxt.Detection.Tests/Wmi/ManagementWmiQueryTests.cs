using DrDxt.Core;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Wmi;

public class ManagementWmiQueryTests
{
    [Fact]
    public void Invalid_wql_fails_cleanly_without_throwing()
    {
        var sut = new ManagementWmiQuery();

        var act = () => sut.Query("THIS IS NOT WQL");

        var r = act.Should().NotThrow().Subject;
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.WmiFail);
    }

    [Fact]
    public void Real_processor_query_returns_at_least_one_row()
    {
        // Test d'intégration léger : tourne sur tout Windows, sans admin.
        var sut = new ManagementWmiQuery();

        var r = sut.Query("SELECT Name FROM Win32_Processor");

        r.IsOk.Should().BeTrue();
        r.Value!.Should().NotBeEmpty();
    }
}
