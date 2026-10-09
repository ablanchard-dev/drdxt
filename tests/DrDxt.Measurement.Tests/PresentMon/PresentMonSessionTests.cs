using DrDxt.Core;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using Xunit;

namespace DrDxt.Measurement.Tests.PresentMon;

/// <summary>
/// Integration-flavored tests against the real <see cref="PresentMonApi2"/> P/Invoke layer.
/// On a box without the PresentMon service these must degrade cleanly (never throw),
/// which also exercises the real DllImport boundary at least for the no-service path.
/// Behavioral capture/parse coverage lives in <see cref="PresentMonSessionCaptureTests"/> (fake api).
/// </summary>
public class PresentMonSessionTests
{
    private static PresentMonSession RealSession() => new(new PresentMonApi2(), new StopwatchClock());

    [Fact]
    public void Service_availability_check_never_throws()
    {
        var sut = RealSession();
        var act = () => sut.IsServiceAvailable();
        var r = act.Should().NotThrow().Subject;
        // CI / dev box without the PresentMon service → Ok(false), never a crash.
        r.IsOk.Should().BeTrue();
    }

    [Fact]
    public async Task Capture_without_service_fails_cleanly_not_throw()
    {
        var sut = RealSession();
        // Nonexistent PID + (likely) no service → clean failure, never an exception.
        var r = await sut.CaptureAsync(int.MaxValue, TimeSpan.FromMilliseconds(50), default);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().BeOneOf(
            ErrorCodes.PmonUnavailable, ErrorCodes.CaptureFail, ErrorCodes.NoFrames, ErrorCodes.PidInvalid);
    }

    [Fact]
    public async Task Capture_honors_cancellation()
    {
        var sut = RealSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await sut.CaptureAsync(1234, TimeSpan.FromSeconds(10), cts.Token);
        r.IsOk.Should().BeFalse(); // cancelled cleanly
    }
}
