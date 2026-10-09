using DrDxt.Core;
using DrDxt.Measurement;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Measurement.Tests.PresentMon;

/// <summary>
/// « Service PresentMon indisponible » ne dit rien de ce qu'il faut réparer.
/// Constaté sur la machine de dev le 27/08/2026 : le service tournait, le tube était
/// joignable, et la capture échouait quand même — le vrai code (PM_STATUS_PIPE_ERROR)
/// était écrasé par un booléen. Deux causes très différentes (DLL absente / service
/// concurrent d'une autre version) rendaient exactement le même message.
/// Ces tests exigent que la cause survive jusqu'à l'utilisateur.
/// </summary>
public class PmonUnavailableDetailTests
{
    private sealed class ZeroClock : IMonotonicClock
    {
        public TimeSpan Elapsed => TimeSpan.Zero;
        public void Restart() { }
    }

    private static PresentMonSession Sut(IPresentMonApi api) => new(api, new ZeroClock());

    [Fact]
    public void Open_session_error_names_the_pm_status()
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(PM_STATUS.PM_STATUS_PIPE_ERROR);

        var sut = Sut(api);
        sut.IsServiceAvailable().Value.Should().BeFalse();
        sut.LastUnavailableDetail.Should().Contain("PIPE_ERROR");
    }

    [Fact]
    public void Two_different_causes_do_not_produce_the_same_detail()
    {
        var refused = Substitute.For<IPresentMonApi>();
        refused.OpenSession(out Arg.Any<nint>()).Returns(PM_STATUS.PM_STATUS_PIPE_ERROR);
        var noDll = Substitute.For<IPresentMonApi>();
        noDll.OpenSession(out Arg.Any<nint>()).Returns(_ => throw new DllNotFoundException("x"));

        var a = Sut(refused); a.IsServiceAvailable();
        var b = Sut(noDll);   b.IsServiceAvailable();

        a.LastUnavailableDetail.Should().NotBeNullOrWhiteSpace();
        b.LastUnavailableDetail.Should().NotBeNullOrWhiteSpace();
        a.LastUnavailableDetail.Should().NotBe(b.LastUnavailableDetail);
    }

    [Fact]
    public void Available_leaves_no_stale_detail()
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(c => { c[0] = (nint)7; return PM_STATUS.PM_STATUS_SUCCESS; });

        var sut = Sut(api);
        sut.IsServiceAvailable().Value.Should().BeTrue();
        sut.LastUnavailableDetail.Should().BeNull();
    }

    [Fact]
    public async Task Runner_failure_message_carries_the_detail()
    {
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(false));
        s.LastUnavailableDetail.Returns("PM_STATUS=12 (PM_STATUS_PIPE_ERROR)");

        var r = await new BenchmarkRunner(s).RunAsync(1234, TimeSpan.FromMilliseconds(10), 1, default);

        r.Error!.Value.Code.Should().Be(ErrorCodes.PmonUnavailable);
        r.Error!.Value.Message.Should().Contain("PIPE_ERROR");
    }

    [Fact]
    public async Task Runner_message_stays_clean_when_no_detail_is_known()
    {
        var s = Substitute.For<IPresentMonSession>();
        s.IsServiceAvailable().Returns(Result<bool>.Ok(false));
        s.LastUnavailableDetail.Returns((string?)null);

        var r = await new BenchmarkRunner(s).RunAsync(1234, TimeSpan.FromMilliseconds(10), 1, default);

        r.Error!.Value.Message.Should().NotContain("Cause :");
    }
}
