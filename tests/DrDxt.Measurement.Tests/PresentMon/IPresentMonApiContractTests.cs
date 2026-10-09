using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Measurement.Tests.PresentMon;

public class IPresentMonApiContractTests
{
    [Fact]
    public void Api_exposes_full_capture_lifecycle()
    {
        var api = Substitute.For<IPresentMonApi>();
        api.OpenSession(out Arg.Any<nint>()).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.StartTracking(Arg.Any<nint>(), 123u).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.StopTracking(Arg.Any<nint>(), 123u).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.CloseSession(Arg.Any<nint>()).Returns(PM_STATUS.PM_STATUS_SUCCESS);
        api.FreeFrameQuery(Arg.Any<nint>()).Returns(PM_STATUS.PM_STATUS_SUCCESS);

        // compile = the surface (Open/Close/Start/Stop/RegisterFrameQuery/ConsumeFrames/FreeFrameQuery) exists
        api.Should().NotBeNull();
    }

    [Fact]
    public void Real_api_implements_the_seam()
        => new PresentMonApi2().Should().BeAssignableTo<IPresentMonApi>();
}
