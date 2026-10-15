using System.Runtime.InteropServices;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

/// <summary>Le VRR lu au pilote. Un bit mal place confondrait « l'ecran sait » et
/// « G-Sync est actif », exactement la difference que le levier de cadence doit faire.</summary>
public class VrrReadingTests
{
    [Fact]
    public void Les_bits_suivent_l_ordre_de_NV_GET_VRR_INFO_V1()
    {
        // enabled(0) possible(1) requested(2) indicator(3) inVrrMode(4)
        VrrReading.Decoder(0b00010).Should().Be(new VrrReading(Possible: true, Enabled: false, Requested: false, InVrrMode: false));
        VrrReading.Decoder(0b00011).Should().Be(new VrrReading(true, true, false, false));
        VrrReading.Decoder(0b11111).Should().Be(new VrrReading(true, true, true, true));
        VrrReading.Decoder(0b01000).Should().Be(new VrrReading(false, false, false, false), "l'indicateur n'est rien de ce qu'on lit");
    }

    [Fact]
    public void La_structure_fait_24_octets_comme_celle_du_pilote()
        => Marshal.SizeOf<NvApiNative.NvGetVrrInfoV1>().Should().Be(24);
}
