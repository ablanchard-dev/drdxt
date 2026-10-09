using DrDxt.Core;
using DrDxt.Detection.Wmi;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Wmi;

public class IWmiQueryContractTests
{
    [Fact]
    public void Query_returns_result_of_rows()
    {
        var sut = Substitute.For<IWmiQuery>();
        sut.Query("SELECT * FROM Win32_Processor")
           .Returns(Result<IReadOnlyList<WmiRow>>.Ok(new List<WmiRow>
           {
               new(new Dictionary<string, object?> { ["Name"] = "AMD Ryzen 7 7800X3D" })
           }));

        var r = sut.Query("SELECT * FROM Win32_Processor");

        r.IsOk.Should().BeTrue();
        r.Value!.Single().GetString("Name").Should().Be("AMD Ryzen 7 7800X3D");
    }
}
