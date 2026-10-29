using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Scoring;

/// <summary>
/// Sur un X3D à deux CCD, la frontière entre le CCD à cache et le CCD de fréquence se compte en processeurs logiques. Sans
/// eux (Win32_Processor muet : CpuDetector rend 0), un cœur du CCD à cache parqué ne se distingue plus d'un routage sain.
/// </summary>
public class X3dSansProcesseursLogiquesLusTests
{
    private static HardwareInventory Hw(int coeurs, int logiques) => new(
        new CpuInfo("AMD Ryzen 9 7950X3D 16-Core Processor", "AMD", coeurs, logiques, true, 2),
        new[] { new GpuInfo("RTX 4080", "NVIDIA", 16L << 30, "616.56", TriState.On) },
        new[] { new MemoryModule("F5-6000J3038F16G", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "ROG STRIX X670E-E"),
        new BiosInfo("1.0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    // Valeurs inventées : les cœurs 2 et 3 sont sur le CCD à cache d'un 7950X3D (logiques 0 à 15).
    private static SettingsState Parques(params int[] coeurs) => new(TriState.On, TriState.On, "381b4222-f694-41f0-9685-ff5bb260df2e",
        TriState.On, GameMode: TriState.On, ParkedCoreIndexes: coeurs, Vrr: TriState.Unknown);

    [Fact]
    public void Temoin_lu_un_coeur_du_ccd_a_cache_parque_est_signale()
        => LeverCatalog.Evaluate(Hw(16, 32), Parques(2, 3)).Single(l => l.LeverId == "x3d-ccd").State
            .Should().Be(LeverState.Suboptimal);

    [Fact]
    public void Sans_processeurs_logiques_lus_le_routage_ne_se_dit_pas_sain()
        => LeverCatalog.Evaluate(Hw(0, 0), Parques(2, 3)).Single(l => l.LeverId == "x3d-ccd").State
            .Should().Be(LeverState.Unknown);
}
