using DrDxt.Detection.Gpu;
using DrDxt.Detection.Gpu.Bench;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// Le banc de stabilité charge l'adaptateur « haute performance » de DXGI, et rien ne relie de façon sûre l'index de la carte
// réglée (NVAPI, ADLX) à un adaptateur DXGI. Avec plus d'une carte dédiée, il ne sait pas laquelle il charge : pas de verdict,
// jamais « stable » pour une carte peut-être jamais chargée. Une puce intégrée à côté ne compte pas.
public sealed class BancSurPlusieursCartesTests
{
    private const long Go = 1L << 30;

    [Fact]
    public void Deux_cartes_dediees_le_banc_ne_tranche_pas()
        => VorticeGpuComputeRunner.PlusieursCartesDediees([
            new DxgiAdapter("NVIDIA GeForce RTX 4060", 0x10DE, 8 * Go),
            new DxgiAdapter("AMD Radeon RX 6600", 0x1002, 8 * Go)]).Should().BeTrue();

    [Fact]
    public void Une_carte_et_une_puce_integree_le_banc_tranche()
        => VorticeGpuComputeRunner.PlusieursCartesDediees([
            new DxgiAdapter("AMD Radeon(TM) 780M Graphics", 0x1002, 2 * Go),
            new DxgiAdapter("NVIDIA GeForce RTX 4060", 0x10DE, 8 * Go),
            new DxgiAdapter("Microsoft Basic Render Driver", 0x1414, 0)]).Should().BeFalse();
}
