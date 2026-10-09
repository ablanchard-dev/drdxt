using DrDxt.App;
using DrDxt.App.Platform;
using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;
using DrDxt.Detection.Gpu.Bench;
using DrDxt.Detection.Gpu.Nvapi;
using DrDxt.GpuNative.NvApi;
using DrDxt.Reconciler;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// M2 H2 — App composition root. AddDrDxtApp() composes Core/Detection/Measurement
// AND the P2 application layer (AddDrDxtApplication), and registers the
// DashboardViewModel so a built provider can resolve the apply/dashboard graph.
// -----------------------------------------------------------------------
public sealed class AppServiceCollectionExtensionsTests
{
    private static ServiceProvider Build() =>
        new ServiceCollection().AddDrDxtApp().BuildServiceProvider();

    [Fact]
    public void Resolves_DashboardViewModel()
    {
        using var provider = Build();
        provider.GetService<DashboardViewModel>().Should().NotBeNull();
    }

    [Fact]
    public void Resolves_ScanViewModel_still()
    {
        using var provider = Build();
        provider.GetService<ScanViewModel>().Should().NotBeNull();
    }

    [Fact]
    public void Resolves_the_firmware_trip_so_the_BIOS_loop_is_not_silently_dead()
    {
        // L0.2 — le defaut d'origine dans sa forme la plus discrete : IBiosLoopVerifier et
        // IPendingBiosChangeStore etaient enregistres en DI et resolus par personne. Le produit
        // ne prouvait a aucun client qu'un changement BIOS avait pris, et rien n'echouait.
        //
        // Resoudre le VM ne suffit pas a l'exclure : son parametre est optionnel, donc un
        // enregistrement manquant donnerait un ScanViewModel parfaitement resolu et muet. Ce
        // test resout la couture elle-meme.
        using var provider = Build();
        provider.GetService<DrDxt.App.Platform.IFirmwareTrip>().Should().NotBeNull();

        // Et surtout : que le VM la RECOIT. Ecrit d'abord sans cette ligne, et une mutation a
        // survecu -- supprimer l'argument du cablage laissait la couture parfaitement resolvable
        // et le ScanViewModel parfaitement muet. « Enregistre » n'est pas « branche ».
        provider.GetRequiredService<ScanViewModel>().CanProveBiosChanges
            .Should().BeTrue("un scan qui ne peut rien prouver du BIOS le doit a un cablage, pas au hasard");
    }

    [Fact]
    public void The_REAL_display_seam_wins_over_the_fail_clean_stub()
    {
        // Trouve par la revue des 5 tours. Si le stub gagnait, le levier de frequence d'affichage
        // et le verdict de cap d'images ne se declencheraient jamais en production -- muets,
        // verts en test, invisibles. Une ligne d'assertion ferme la question.
        //
        // CORRECTION DU 05/09/2026 — le MECANISME ecrit ici etait faux, et un mecanisme faux
        // dans le commentaire d'une garde est la maniere dont le suivant la casse.
        // Il disait : « Win32DisplayInfo est enregistre apres, et en DI Microsoft c'est le dernier
        // inscrit qui gagne ». Mesure : `RegisterRealSeams` est appele ligne 55, avant
        // `AddDrDxtApplication()` ligne 60. La vraie couture gagne donc parce qu'elle est inscrite
        // EN premier et que le stub arrive en `TryAdd`, qui cede a ce qui existe deja.
        //
        // La garantie tient aujourd'hui par deux raisons independantes (l'ordre, et le TryAdd du
        // stub) : c'est pour ca que transformer le `AddSingleton` du vrai seam en `TryAddSingleton`
        // ne change rien — mutation equivalente, verifiee. Ce qui la casse vraiment, et ce que ce
        // test attrape (verifie par mutation le 05/09) : que le stub cesse d'etre un TryAdd.
        using var provider = Build();

        provider.GetRequiredService<DrDxt.Application.Tweaks.Hardware.IDisplayInfo>()
            .Should().BeOfType<DrDxt.Detection.Display.Win32DisplayInfo>(
                "sans la vraie sonde d'affichage, deux leviers deviennent silencieux");
    }

    [Fact]
    public void The_settings_reader_RECEIVES_the_running_game_so_driver_settings_are_read_at_game_scope()
    {
        // PHASE 18 F1 — le parametre du lecteur est optionnel : un enregistrement manquant donnerait
        // un lecteur parfaitement resolu qui lit le profil GLOBAL pour tous les jeux, sans rien dire.
        using var provider = Build();

        provider.GetRequiredService<IGameExecutable>().Should().BeOfType<RunningGameExecutable>();
        provider.GetRequiredService<ISettingsStateReader>()
            .Should().BeOfType<DrDxt.Detection.Settings.SettingsStateReader>()
            .Which.ReadsAtGameScope.Should().BeTrue("sans le jeu, le gate du pilote lit le mauvais profil");
    }

    /// <summary>24/09/2026 : un levier DRS sans le jeu écrit au profil global, pour tout le PC. Le
    /// paramètre est optionnel : un câblage raté ne se verrait nulle part ailleurs.</summary>
    [Theory]
    [InlineData("latency.prefer_max_perf")]
    [InlineData("latency.render_queue_cap")]
    [InlineData("latency.vrr_vsync_cap")]
    public void Les_leviers_DRS_du_produit_recoivent_le_jeu(string id)
    {
        using var provider = Build();
        var levier = provider.GetServices<ITweak>().Single(t => t.Id == id);

        levier.GetType()
            .GetField("_game", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(levier).Should().BeOfType<RunningGameExecutable>();
    }

    [Fact]
    public void Resolves_application_layer_engine_and_tweaks()
    {
        using var provider = Build();
        provider.GetService<ITweakEngine>().Should().NotBeNull();
        provider.GetServices<ITweak>().Should().NotBeEmpty();
        provider.GetService<ITweakCatalog>().Should().NotBeNull();
        provider.GetService<ISafetyManager>().Should().NotBeNull();
    }

    // R1 — the App composition root replaces the fail-clean stubs with the real
    // Windows-backed seams + the signed-IPC elevated client (not the stubs).
    [Fact]
    public void Resolves_REAL_windows_seams_not_stubs()
    {
        using var provider = Build();
        provider.GetService<IRestorePointService>().Should().BeOfType<WmiRestorePointService>();
        provider.GetService<IConsentPrompt>().Should().BeOfType<MessageBoxConsentPrompt>();
        provider.GetService<IRegistryReader>().Should().BeOfType<Win32RegistryReader>();
        provider.GetService<IPowerPlanReader>().Should().BeOfType<Win32PowerPlanReader>();
        provider.GetService<IServiceStateReader>().Should().BeOfType<Win32ServiceStateReader>();
    }

    [Fact]
    public void Resolves_an_IElevatedClient()
    {
        // The factory returns the real signed-IPC client when a session is
        // provisioned, else a fail-clean client — either way the graph resolves
        // one (never null), so an apply path can be wired.
        using var provider = Build();
        provider.GetService<IElevatedClient>().Should().NotBeNull();
    }

    // R2 — the App composition root wires the real deterministic GPU bench: the
    // DeterministicGpuLoadProbe over the real Vortice D3D11 compute runner, replacing
    // the fail-clean StubGpuLoadProbe. The stability tester stays GpuStabilityTester
    // (its fail-safe Inconclusive→revert semantics are preserved).
    [Fact]
    public void Resolves_REAL_gpu_load_probe_not_stub()
    {
        using var provider = Build();
        provider.GetService<IGpuComputeRunner>().Should().BeOfType<VorticeGpuComputeRunner>();
        provider.GetService<IGpuLoadProbe>().Should().BeOfType<DeterministicGpuLoadProbe>();
        provider.GetService<IStabilityTester>().Should().BeOfType<GpuStabilityTester>();
    }

    // T5 — the App composition root wires the real ADLX read seam: IAdlxGpuTuning →
    // AdlxGpuTuningReader over the real AdlxNative P/Invoke boundary (validated on a real
    // AMD RX 9070 XT, 76d225d), REPLACING the fail-clean StubAdlxGpuTuning. On AMD this
    // makes IsAvailable true so GpuUndervolt/GpuPowerLimit.ApplyAmdAsync fires; on a non-AMD
    // machine (this CI box / the reference NVIDIA machine) the native boundary's lazy probe
    // finds no amdadlx64.dll → IsAvailable == false and the AMD apply path degrades clean.
    // SAFETY-CRITICAL: resolving the seam and reading IsAvailable must not throw on non-AMD.
    [Fact]
    public void Resolves_REAL_adlx_read_seam_not_stub()
    {
        using var provider = Build();

        provider.GetService<IAdlxNative>().Should().BeOfType<AdlxNative>();

        var adlx = provider.GetService<IAdlxGpuTuning>();
        adlx.Should().BeOfType<AdlxGpuTuningReader>(
            "T5 wires the real reader in place of the fail-clean StubAdlxGpuTuning");

        // Non-AMD no-crash guarantee: lazily probing the (absent) ADLX DLL must not throw;
        // it simply reports unavailable. This is the property that keeps the seam safe to
        // ship to a non-AMD machine.
        bool available = true;
        var read = () => available = adlx!.IsAvailable;
        read.Should().NotThrow("the ADLX boundary is fail-clean — no AMD driver must never crash");
        available.Should().BeFalse("no AMD GPU / amdadlx64.dll on this machine → IsAvailable is false");
    }

    // N4 — the App composition root wires the real NVAPI read seam: INvApiGpuTuning →
    // NvApiGpuTuningReader over the real NvApiNative P/Invoke boundary (the NVAPI write was
    // live-validated on an RTX 4060, 11b6b41), REPLACING the fail-clean StubNvApiGpuTuning.
    // On NVIDIA this makes IsAvailable true so GpuUndervolt/GpuPowerLimit.ApplyNvidiaAsync
    // fires; on a non-NVIDIA box nvapi64.dll is absent → IsAvailable == false and the NVIDIA
    // apply path degrades clean. SAFETY-CRITICAL: resolving the seam and reading IsAvailable
    // must not throw — this same binary ships to machines with no NVIDIA GPU. We cannot pin
    // IsAvailable's value (the build/dev box may HAVE an NVIDIA GPU), so we assert the real
    // concrete types + that the read is exception-free and returns a readable bool.
    [Fact]
    public void Resolves_REAL_nvapi_read_seam_not_stub()
    {
        using var provider = Build();

        provider.GetService<INvApiNative>().Should().BeOfType<NvApiNative>();

        var nvapi = provider.GetService<INvApiGpuTuning>();
        nvapi.Should().BeOfType<NvApiGpuTuningReader>(
            "N4 wires the real reader in place of the fail-clean StubNvApiGpuTuning");

        // No-crash guarantee: lazily probing nvapi64.dll (present or absent) must not throw;
        // it simply reports availability. This is the property that keeps the seam safe to
        // ship to a non-NVIDIA machine. The value is hardware-dependent so we do not pin it.
        bool? available = null;
        var read = () => available = nvapi!.IsAvailable;
        read.Should().NotThrow("the NVAPI boundary is fail-clean — no NVIDIA driver must never crash");
        available.Should().NotBeNull("IsAvailable resolved to a readable bool (true on NVIDIA, false otherwise)");
    }

    // 11/09/2026 — L'INVERSE de l'ancien test. Il exigeait que l'App resolve tout le graphe
    // DesiredStateReconciler ; or aucun code de l'App ne le resolvait jamais. Un enregistrement
    // mort n'est pas inoffensif : le jour ou quelqu'un le resout par erreur, un SECOND ecrivain
    // NON ELEVE reecrit la machine a cote du service, et rien ne rougit. Le seul reconciliateur
    // est celui du service eleve (DrDxt.ReconcilerHost).
    [Fact]
    public void The_App_registers_NO_reconciler_so_no_second_non_elevated_writer_can_exist()
    {
        using var provider = Build();
        provider.GetService<IReconciler>().Should().BeNull();
        provider.GetService<DesiredStateReconciler>().Should().BeNull();
        provider.GetService<IReconcilerStateStore>().Should().BeNull();
        provider.GetService<IBootOutcomeStore>().Should().BeNull();
        provider.GetService<BootLoopGuard>().Should().BeNull();
    }

    // M4 — the App wires the VERIFY-only desired-state store (read/show drift) and a
    // notifier. The signer / private-key custody (commit path) is M5 and is not wired
    // here, so this surface can only read; resolution must still succeed.
    [Fact]
    public void Resolves_verify_only_desired_state_store_and_notifier()
    {
        using var provider = Build();
        provider.GetService<IDesiredStateStore>().Should().NotBeNull();
        provider.GetService<IReconcilerNotifier>().Should().BeOfType<AppReconcilerNotifier>();
    }

}
