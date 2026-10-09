using DrDxt.Detection.Settings;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Settings;

public class RegistryProviderTests
{
    [Fact]
    public void Hags_read_never_throws_returns_tristate()
    {
        var sut = new RegistryProvider();
        var act = () => sut.GetHags();
        var r = act.Should().NotThrow().Subject;
        r.Should().BeOneOf(TriStateValues());
    }

    [Fact]
    public void Game_mode_read_never_throws()
    {
        var sut = new RegistryProvider();
        sut.Invoking(s => s.GetGameMode()).Should().NotThrow();
    }

    [Fact]
    public void Parked_cores_read_returns_list_never_null()
    {
        var sut = new RegistryProvider();
        sut.GetParkedCoreIndexes().Should().NotBeNull();
    }

    [Fact]
    public void Vrr_read_never_throws_returns_tristate()
    {
        // Windows "Variable refresh rate" toggle — may or may not be set → honest Unknown
        var sut = new RegistryProvider();
        var act = () => sut.GetVrr();
        var r = act.Should().NotThrow().Subject;
        r.Should().BeOneOf(TriStateValues());
    }

    private static DrDxt.Core.TriState[] TriStateValues() =>
        new[] { DrDxt.Core.TriState.On, DrDxt.Core.TriState.Off, DrDxt.Core.TriState.Unknown };
}
