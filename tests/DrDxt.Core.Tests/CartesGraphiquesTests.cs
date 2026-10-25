using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Une seule règle de « carte dédiée » pour tout le produit. La mémoire vidéo seule ne suffit pas : une APU AMD réglée avec une
// réserve UMA de 2 Go rend 2 Go de mémoire dédiée par DXGI. Le nom tranche alors : une puce intégrée AMD s'appelle
// « … Graphics » sans RX, Pro ni Instinct ; une Intel dédiée est une Arc A ou B suivie de son numéro.
public sealed class CartesGraphiquesTests
{
    private const long Mo = 1L << 20;
    private const long Go = 1L << 30;

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060", "NVIDIA", 8 * Go, true)]
    [InlineData("AMD Radeon RX 7800 XT", "AMD", 16 * Go, true)]
    [InlineData("AMD Radeon Pro W7600", "AMD", 8 * Go, true)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "Intel", 12 * Go, true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Intel", 16 * Go, true)]
    [InlineData("Radeon (TM) RX 480 Graphics", "AMD", 8 * Go, true)]
    [InlineData("AMD Radeon RX 6800M", "AMD", 12 * Go, true)]
    [InlineData("AMD Radeon(TM) 780M Graphics", "AMD", 2 * Go, false)]
    [InlineData("AMD Radeon(TM) RX Vega 11 Graphics", "AMD", 2 * Go, false)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", "AMD", 2 * Go, false)]
    [InlineData("AMD Radeon(TM) 610M", "AMD", 2 * Go, false)]
    [InlineData("AMD Radeon(TM) Graphics", "AMD", 512 * Mo, false)]
    [InlineData("Intel(R) Arc(TM) Graphics", "Intel", 2 * Go, false)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "Intel", 1 * Go, false)]
    [InlineData("Intel(R) UHD Graphics 770", "Intel", 128 * Mo, false)]
    public void Une_carte_est_dediee_par_sa_memoire_et_son_nom(string modele, string vendeur, long vram, bool dediee)
        => CartesGraphiques.EstDediee(new GpuInfo(modele, vendeur, vram, "1.0", TriState.Unknown)).Should().Be(dediee);

    // « ATI » est une sous-chaîne de « NVIDIA Corporation » et « Intel Corporation ».
    [Theory]
    [InlineData("NVIDIA Corporation", false)]
    [InlineData("Intel Corporation", false)]
    [InlineData("NVIDIA", false)]
    [InlineData("GenuineIntel", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("ATI Technologies Inc.", true)]
    [InlineData("Advanced Micro Devices, Inc.", true)]
    [InlineData("AuthenticAMD", true)]
    [InlineData("AMD", true)]
    public void NVIDIA_Corporation_n_est_pas_AMD(string? constructeur, bool amd)
        => CartesGraphiques.EstAmd(constructeur).Should().Be(amd);
}
