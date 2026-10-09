using DrDxt.Detection.Gpu;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Gpu;

public class VorticeDxgiProviderTests
{
    [Fact]
    public void Enumerates_at_least_one_adapter_on_real_machine_without_throwing()
    {
        var sut = new VorticeDxgiProvider();
        var act = () => sut.EnumerateAdapters();
        var r = act.Should().NotThrow().Subject;
        r.IsOk.Should().BeTrue();
        r.Value!.Should().NotBeEmpty(); // au moins le Microsoft Basic Render Driver
    }
}
