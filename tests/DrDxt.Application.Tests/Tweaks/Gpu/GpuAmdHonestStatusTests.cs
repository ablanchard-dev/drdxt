using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// La porte de câblage de production du statut honnête « GPU AMD détecté, réglage à venir ».
//
// Garde de régression sur un vrai défaut : le statut honnête dépendait d'un drapeau porté par
// la couture ADLX, dont l'implémentation de production (StubAdlxGpuTuning) rend une constante
// fausse ; sur une vraie machine AMD le statut était mort, et un faux devait forcer le drapeau
// pour que les tests unitaires passent (un faux positif). Le correctif place « GPU AMD présent »
// sur l'IGpuTuningTargetSource alimenté par le matériel (calculé via GpuGating.IsAmd), dont
// l'implémentation de production reflète le vrai matériel.
//
// Ce test compose le vrai graphe d'application de production (AddDrDxtApplication → les
// vrais StubNvApiGpuTuning + StubAdlxGpuTuning, tous deux IsAvailable == false, exactement
// comme sur une vraie machine) et un HardwareInventory qui contient un GPU AMD. Sans rien
// forcer sur aucun faux, chaque levier GPU doit rendre le code honnête explicite : preuve que
// le statut joue sous le vrai câblage.
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

    // Le vrai graphe d'application de production : les bouchons SDK à échec propre (NVAPI et ADLX
    // tous deux IsAvailable == false) et le vrai singleton GpuTuningTargetSource.
    private static ServiceProvider BuildProductionGraph() =>
        new ServiceCollection().AddDrDxtApplication().BuildServiceProvider();

    [Fact]
    public async Task With_real_prod_stubs_and_amd_hardware_every_gpu_tweak_returns_honest_amd_status()
    {
        using var provider = BuildProductionGraph();

        // Seule « amdité » injectée : la vraie source apprend le GPU AMD du matériel scanné
        // (exactement ce que fait ScanProfileConsumer au scan). Aucun faux n'est forcé.
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
            // Distinct du générique « SDK indisponible » qui joue sur les machines non AMD.
            r.Error!.Value.Code.Should().NotBe(ErrorCodes.TweakSdkUnavailable);
        }
    }

    [Fact]
    public async Task With_real_prod_stubs_and_non_amd_hardware_gpu_tweaks_do_not_show_the_amd_status()
    {
        // Frontière d'honnêteté : une machine non AMD ne doit pas voir le message AMD. Sans GPU AMD,
        // la présence côté source reste fausse → les leviers retombent sur le générique
        // TWEAK.SDK_UNAVAILABLE (le SDK n'est vraiment pas câblé), jamais la ligne AMD.
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
