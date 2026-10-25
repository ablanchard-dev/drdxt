using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

// Un réglage DRS binaire porte sa longueur dans les quatre premiers octets, une chaîne deux caractères : les lire comme un
// DWORD rendrait un nombre sans sens présenté comme lu.
public sealed class LectureDrsTypeTests
{
    private static readonly byte[] Valeur = [0x2A, 0, 0, 0];

    [Fact]
    public void Un_DWORD_se_lit_avec_sa_provenance()
        => NvApiNative.Decoder(0, Valeur, 1, 1, 1).Should().Be(new DrsReading(42, 1, 1, 1));

    [Theory]
    [InlineData(1u)] // NVDRS_BINARY_TYPE
    [InlineData(3u)] // NVDRS_WSTRING_TYPE
    public void Un_autre_type_n_est_pas_lu(uint type)
        => NvApiNative.Decoder(type, Valeur, 1, 1, 1).Should().BeNull();
}
