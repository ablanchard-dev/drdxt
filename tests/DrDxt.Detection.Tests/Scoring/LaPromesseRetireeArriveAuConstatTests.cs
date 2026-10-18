using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Scoring;

// La mesure dit qu'un levier ne rendra pas d'images ici (GPU saturé : XMP n'en donnera pas) : le constat le porte, dans la
// langue du docteur, pour que l'écran ne promette pas un gain que la mesure dément. Sans mesure, rien n'est retiré.
public sealed class LaPromesseRetireeArriveAuConstatTests
{
    private static HardwareInventory Hw() => new(
        new CpuInfo("CPU", "AMD", 8, 16, IsX3D: true, CcdCount: 2),
        [new GpuInfo("GPU", "NVIDIA", 8L << 30, "551.86", TriState.Off)],
        [new MemoryModule("PN", "G.Skill", 4000, 3800)],
        new MotherboardInfo("MSI", "PRO B760-P"),
        new BiosInfo("1.H0", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static SettingsState XmpCoupe() => new(XmpExpo: TriState.Off, ReBar: TriState.On,
        PowerPlanGuid: "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", Hags: TriState.On, GameMode: TriState.On,
        ParkedCoreIndexes: [], Vrr: TriState.Unknown);

    private static BenchmarkResult GpuSature() => new(AvgFps: 142.9, P1LowFps: 120, P01LowFps: 90,
        FrameTimesMs: Enumerable.Repeat(7.0, 600).ToList(), EstimatedPcLatencyMs: 3.0, MarginOfError: 1.0, Runs: 5,
        MeanGpuBusyMs: 7.0 * 0.96, GpuWaitRatio: 0.03, PresentStallMsP95: 0.2, RenderPresentLatencyMsP95: 3.0,
        AnimationErrorMsP95: 0.5, CommitPressure: 0.40);

    private static Finding Xmp(BenchmarkResult? mesure, string langue)
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo(langue));
        return new ScanScorer().Score(Hw(), XmpCoupe(), mesure).Value!.Findings.Single(f => f.TitleKey == "Finding.Memory.XmpOff");
    }

    [Fact]
    public void Sur_un_gpu_sature_le_constat_xmp_dit_qu_il_ne_rendra_pas_d_images_dans_la_langue_du_docteur()
    {
        Xmp(GpuSature(), "fr-FR").GainSuppressedReason.Should().Contain("images");
        Xmp(GpuSature(), "en-US").GainSuppressedReason.Should().Contain("frames");
    }

    [Fact]
    public void Sans_mesure_rien_n_est_retire() => Xmp(null, "fr-FR").GainSuppressedReason.Should().BeNull();
}
