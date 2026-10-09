using DrDxt.Core;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Measurement.Tests.PresentMon;

public class IPresentMonSessionContractTests
{
    [Fact]
    public async Task Session_returns_frametimes_for_a_pid()
    {
        var s = Substitute.For<IPresentMonSession>();
        s.CaptureAsync(1234, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
         .Returns(Result<FrameCapture>.Ok(new FrameCapture(new List<double> { 8.3, 8.4, 8.2 }, Array.Empty<double>(), Array.Empty<double>())));

        var r = await s.CaptureAsync(1234, TimeSpan.FromSeconds(1), default);

        r.IsOk.Should().BeTrue();
        r.Value!.FrameTimesMs.Should().HaveCount(3);
    }
}
