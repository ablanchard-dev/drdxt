using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// The prod-wiring gate for the honest "AMD GPU detected — tuning coming soon" status.
//
// Regression guard for a real defect: the honest status was once gated on a flag that
// lived on the ADLX seam, whose PRODUCTION impl (StubAdlxGpuTuning) returns a constant
// false — so on a real AMD machine the status was DEAD and a fake had to force the flag
// for the unit tests to pass (a false-positive). The fix moves "AMD GPU present" onto the
// hardware-fed IGpuTuningTargetSource (computed from hw via GpuGating.IsAmd), whose prod
// impl reflects the TRUE hardware.
//
// This test composes the ACTUAL production application graph (AddDrDxtApplication → the
// Real StubNvApiGpuTuning + StubAdlxGpuTuning, both IsAvailable == false, exactly as on a
// real machine today) and a HardwareInventory that contains an AMD GPU. With nothing
// forced on any fake, every GPU tweak must return the explicit honest code — proving the
// status fires under real wiring.
// -----------------------------------------------------------------------
public sealed class GpuAmdHonestStatusTests
{
    private static HardwareInventory AmdDesktop() => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo("AMD Radeon RX 7800 XT", "AMD", 16L << 30, "24.1.1", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1.2", null, true),
        ChassisType.Desktop);

    private static HardwareInventory IntelNvidiaDesktop() => new(
        new CpuInfo("Core i5-13600", "Intel", 14, 20, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4070", "NVIDIA", 12L << 30, "551.23", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 5600, 5600) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    // The real production application graph: the actual fail-clean SDK stubs (NVAPI + ADLX
    // both IsAvailable == false) and the real GpuTuningTargetSource singleton.
    private static ServiceProvider BuildProductionGraph() =>
        new ServiceCollection().AddDrDxtApplication().BuildServiceProvider();

    [Fact]
    public async Task With_real_prod_stubs_and_amd_hardware_every_gpu_tweak_returns_honest_amd_status()
    {
        using var provider = BuildProductionGraph();

        // The only "AMD-ness" injected: the real source learns the AMD GPU from the scanned
        // hardware (exactly what ScanProfileConsumer does at scan time). No fake is forced.
        provider.GetRequiredService<IGpuTuningTargetSource>().SetAmdGpuPresenceFromHardware(AmdDesktop());

        var gpuTweaks = provider.GetServices<ITweak>()
            .Where(t => t.Category == TweakCategory.GpuTuning)
            .ToList();
        gpuTweaks.Should().HaveCount(3, "undervolt + power-limit + fan-curve");

        foreach (var tweak in gpuTweaks)
        {
            var r = await tweak.ApplyAsync();

            r.IsOk.Should().BeFalse($"{tweak.Id} must not claim success on an AMD machine with no ADLX");
            r.Error!.Value.Code.Should().Be(ErrorCodes.GpuAmdTuningUnavailable,
                $"{tweak.Id} must surface the explicit honest AMD status, not a silent skip");
            // Distinct from the generic SDK-unavailable that fires on non-AMD machines.
            r.Error!.Value.Code.Should().NotBe(ErrorCodes.TweakSdkUnavailable);
        }
    }

    [Fact]
    public async Task With_real_prod_stubs_and_non_amd_hardware_gpu_tweaks_do_not_show_the_amd_status()
    {
        // Honesty boundary: a non-AMD machine must not see the AMD message. With no AMD GPU,
        // the source's presence stays false → the tweaks fall back to the generic
        // TWEAK.SDK_UNAVAILABLE (the SDK genuinely isn't wired), never the AMD line.
        using var provider = BuildProductionGraph();
        provider.GetRequiredService<IGpuTuningTargetSource>().SetAmdGpuPresenceFromHardware(IntelNvidiaDesktop());

        var gpuTweaks = provider.GetServices<ITweak>()
            .Where(t => t.Category == TweakCategory.GpuTuning)
            .ToList();

        foreach (var tweak in gpuTweaks)
        {
            var r = await tweak.ApplyAsync();

            r.IsOk.Should().BeFalse();
            r.Error!.Value.Code.Should().NotBe(ErrorCodes.GpuAmdTuningUnavailable,
                $"{tweak.Id} must NOT show the AMD status on a non-AMD machine");
            r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        }
    }
}
