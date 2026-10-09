using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DrDxt.Detection.Tests.Gpu;

public class IDxgiProviderContractTests
{
    [Fact]
    public void Provider_returns_adapters_with_dedicated_vram()
    {
        var p = Substitute.For<IDxgiProvider>();
        p.EnumerateAdapters().Returns(Result<IReadOnlyList<DxgiAdapter>>.Ok(new List<DxgiAdapter>
        {
            new("NVIDIA GeForce RTX 4070", 0x10DE, 12L * 1024 * 1024 * 1024)
        }));

        var r = p.EnumerateAdapters();

        r.Value!.Single().DedicatedVramBytes.Should().Be(12L * 1024 * 1024 * 1024);
        r.Value!.Single().VendorId.Should().Be(0x10DE);
    }
}
