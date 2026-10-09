using DrDxt.Core;
using DrDxt.Detection.Gpu;
using DrDxt.GpuNative.Nvml;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Gpu;

public class NvmlReBarProviderTests
{
    private const ulong Mib = 1024UL * 1024UL;
    private const ulong Gib = 1024UL * Mib;

    private sealed class FakeNvmlNative : INvmlNative
    {
        public bool Available;
        public bool ReadSucceeds;
        public ulong Bar1;
        public ulong Fb;

        public bool IsAvailable => Available;

        public bool TryGetBar1AndFbBytes(int deviceIndex, out ulong bar1TotalBytes, out ulong fbTotalBytes)
        {
            bar1TotalBytes = Bar1;
            fbTotalBytes = Fb;
            return ReadSucceeds;
        }

        public bool TryGetPowerUsageMilliwatts(int deviceIndex, out uint milliwatts) { milliwatts = 0; return false; }

        public bool TryGetTemperatureCelsius(int deviceIndex, out uint celsius) { celsius = 0; return false; }

        public bool TryGetDriverVersion(out string version) { version = string.Empty; return false; }

        // Ce faux ne parle que de ReBAR : frequence et raisons de bridage restent NON lues, ce
        // qui est exactement ce qu'une carte sans support rendrait.
        public bool TryGetClockMhz(int deviceIndex, out uint mhz) { mhz = 0; return false; }
        public bool TryGetClocksEventReasons(int deviceIndex, out ulong reasons) { reasons = 0; return false; }
    }

    [Fact]
    public void Unknown_when_nvml_unavailable()
    {
        var sut = new NvmlReBarProvider(new FakeNvmlNative { Available = false });
        sut.GetReBarState().Should().Be(TriState.Unknown);
    }

    [Fact]
    public void On_when_bar1_covers_framebuffer()
    {
        var sut = new NvmlReBarProvider(new FakeNvmlNative
        { Available = true, ReadSucceeds = true, Bar1 = 8192 * Mib, Fb = 8188 * Mib });
        sut.GetReBarState().Should().Be(TriState.On);
    }

    [Fact]
    public void Off_when_bar1_is_legacy_window()
    {
        var sut = new NvmlReBarProvider(new FakeNvmlNative
        { Available = true, ReadSucceeds = true, Bar1 = 256 * Mib, Fb = 8 * Gib });
        sut.GetReBarState().Should().Be(TriState.Off);
    }

    [Fact]
    public void Unknown_when_read_fails()
    {
        var sut = new NvmlReBarProvider(new FakeNvmlNative { Available = true, ReadSucceeds = false });
        sut.GetReBarState().Should().Be(TriState.Unknown);
    }
}
