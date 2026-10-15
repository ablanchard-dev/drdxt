using DrDxt.Core;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using Xunit;

namespace DrDxt.Measurement.Tests.PresentMon;

/// <summary>
/// Tests à saveur d'intégration contre la vraie couche P/Invoke <see cref="PresentMonApi2"/>.
/// Sur une machine sans service PresentMon ils doivent se dégrader proprement (ne jamais lever),
/// ce qui exerce aussi la vraie frontière DllImport, au moins pour le chemin sans service.
/// La couverture de capture et d'analyse vit dans <see cref="PresentMonSessionCaptureTests"/> (fausse API).
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
        // Machine de CI ou de développement sans service PresentMon → Ok(false), jamais un plantage.
        r.IsOk.Should().BeTrue();
    }

    [Fact]
    public async Task Capture_without_service_fails_cleanly_not_throw()
    {
        var sut = RealSession();
        // PID inexistant + (probablement) pas de service → échec propre, jamais une exception.
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
        r.IsOk.Should().BeFalse(); // annulé proprement
    }
}
