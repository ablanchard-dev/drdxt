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
// Racine de composition de l'App. AddDrDxtApp() compose Core/Detection/Measurement
// et la couche application (AddDrDxtApplication).
// -----------------------------------------------------------------------
public sealed class AppServiceCollectionExtensionsTests
{
    private static ServiceProvider Build() =>
        new ServiceCollection().AddDrDxtApp().BuildServiceProvider();

    [Fact]
    public void Resolves_ScanViewModel_still()
    {
        using var provider = Build();
        provider.GetService<ScanViewModel>().Should().NotBeNull();
    }

    [Fact]
    public void Resolves_the_firmware_trip_so_the_BIOS_loop_is_not_silently_dead()
    {
        // Le defaut d'origine dans sa forme la plus discrete : IBiosLoopVerifier et
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
        // Si le stub gagnait, le levier de frequence d'affichage et le verdict de cap d'images ne se
        // declencheraient jamais en production : muets, verts en test, invisibles.
        //
        // Le mecanisme : `RegisterRealSeams` est appele avant `AddDrDxtApplication()`. La vraie
        // couture gagne parce qu'elle est inscrite en premier et que le stub arrive en `TryAdd`,
        // qui cede a ce qui existe deja.
        //
        // La garantie tient par deux raisons independantes (l'ordre, et le TryAdd du stub) : changer
        // le `AddSingleton` du vrai seam en `TryAddSingleton` ne change rien (mutation equivalente,
        // verifiee). Ce que ce test attrape (verifie par mutation) : que le stub cesse d'etre un TryAdd.
        using var provider = Build();

        provider.GetRequiredService<DrDxt.Application.Tweaks.Hardware.IDisplayInfo>()
            .Should().BeOfType<DrDxt.Detection.Display.EcransEnCache>(
                "sans la vraie sonde d'affichage, deux leviers deviennent silencieux");
    }

    [Fact]
    public void The_settings_reader_RECEIVES_the_running_game_so_driver_settings_are_read_at_game_scope()
    {
        // Le parametre du lecteur est optionnel : un enregistrement manquant donnerait
        // un lecteur parfaitement resolu qui lit le profil GLOBAL pour tous les jeux, sans rien dire.
        using var provider = Build();

        provider.GetRequiredService<IGameExecutable>().Should().BeOfType<RunningGameExecutable>();
        provider.GetRequiredService<ISettingsStateReader>()
            .Should().BeOfType<DrDxt.Detection.Settings.SettingsStateReader>()
            .Which.ReadsAtGameScope.Should().BeTrue("sans le jeu, le gate du pilote lit le mauvais profil");
    }

    /// <summary>Le levier d'écran reçoit la source d'alimentation : sans elle (paramètre optionnel), il remonterait la
    /// fréquence d'un portable sur batterie, et rien ailleurs ne le verrait.</summary>
    [Fact]
    public void Le_levier_d_ecran_recoit_la_source_d_alimentation()
    {
        using var provider = Build();
        var levier = provider.GetServices<ITweak>().Single(t => t.Id == "fixit.ecran_frequence");

        levier.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(DrDxt.Application.Tweaks.Hardware.ISourceDAlimentation))
            .GetValue(levier).Should().BeOfType<DrDxt.Detection.Hardware.Win32SourceDAlimentation>();
    }

    /// <summary>Un levier DRS sans le jeu écrit au profil global, pour tout le PC. Le
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

    // Le moteur relit un levier posé sans mesure par ce qui le lit vraiment : la composition lui passe les lecteurs de la
    // carte et du processeur, sinon le plafond de chaleur se dirait « effectif » sans relecture.
    [Fact]
    public void Le_moteur_recoit_les_lecteurs_de_la_carte_et_du_processeur()
    {
        using var provider = Build();
        var moteur = provider.GetRequiredService<ITweakEngine>();

        var carte = (DrDxt.Application.Tweaks.AppliedStateVerifier.LecteursCarte)moteur.GetType()
            .GetField("_carte", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(moteur)!;
        carte.Cpu.Should().NotBeNull();
        carte.Nvidia.Should().NotBeNull();
        carte.Amd.Should().NotBeNull();
        carte.PiloteAmd.Should().NotBeNull();
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

    // La racine de composition de l'App remplace les bouchons à échec propre par les vraies
    // coutures adossées à Windows et le client élevé à IPC signé (pas les bouchons).
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
        // La fabrique rend le vrai client à IPC signé quand une session est provisionnée,
        // sinon un client à échec propre ; dans les deux cas le graphe en résout un
        // (jamais null), pour qu'un chemin d'application puisse être câblé.
        using var provider = Build();
        provider.GetService<IElevatedClient>().Should().NotBeNull();
    }

    // La racine de composition de l'App câble le vrai banc GPU déterministe : le
    // DeterministicGpuLoadProbe sur le vrai exécutant de calcul D3D11 Vortice, à la place du
    // StubGpuLoadProbe à échec propre. Le testeur de stabilité reste GpuStabilityTester
    // (sa sémantique de sûreté Inconclusive → retour arrière est préservée).
    [Fact]
    public void Resolves_REAL_gpu_load_probe_not_stub()
    {
        using var provider = Build();
        provider.GetService<IGpuComputeRunner>().Should().BeOfType<VorticeGpuComputeRunner>();
        provider.GetService<IGpuLoadProbe>().Should().BeOfType<DeterministicGpuLoadProbe>();
        provider.GetService<IStabilityTester>().Should().BeOfType<GpuStabilityTester>();
    }

    // La racine de composition de l'App câble la vraie couture de lecture ADLX : IAdlxGpuTuning →
    // AdlxGpuTuningReader sur la vraie frontière P/Invoke AdlxNative (validée sur une vraie
    // RX 9070 XT AMD), à la place du StubAdlxGpuTuning à échec propre. Sur AMD, IsAvailable
    // devient vrai et GpuUndervolt/GpuPowerLimit.ApplyAmdAsync joue ; sur une machine sans AMD
    // (cette machine de CI, la machine NVIDIA de référence) la sonde paresseuse de la frontière native
    // ne trouve pas amdadlx64.dll → IsAvailable == false et le chemin AMD se dégrade proprement.
    // Critique pour la sécurité : résoudre la couture et lire IsAvailable ne doit pas lever hors AMD.
    [Fact]
    public void Resolves_REAL_adlx_read_seam_not_stub()
    {
        using var provider = Build();

        provider.GetService<IAdlxNative>().Should().BeOfType<AdlxNative>();

        var adlx = provider.GetService<IAdlxGpuTuning>();
        adlx.Should().BeOfType<AdlxGpuTuningReader>(
            "T5 wires the real reader in place of the fail-clean StubAdlxGpuTuning");

        // Garantie sans plantage hors AMD : sonder paresseusement la DLL ADLX (absente) ne doit pas
        // lever ; elle se dit simplement indisponible. C'est la propriété qui rend la couture sûre
        // à livrer sur une machine sans AMD.
        bool available = true;
        var read = () => available = adlx!.IsAvailable;
        read.Should().NotThrow("the ADLX boundary is fail-clean — no AMD driver must never crash");
        // Sur une machine AMD avec Adrenalin, ADLX est là : la valeur ne se fige que là où elle est certaine, sans la DLL.
        if (!System.IO.File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "amdadlx64.dll")))
            available.Should().BeFalse("no amdadlx64.dll on this machine → IsAvailable is false");
    }

    // La racine de composition de l'App câble la vraie couture de lecture NVAPI : INvApiGpuTuning →
    // NvApiGpuTuningReader sur la vraie frontière P/Invoke NvApiNative (l'écriture NVAPI a été
    // validée en direct sur une RTX 4060), à la place du StubNvApiGpuTuning à échec propre.
    // Sur NVIDIA, IsAvailable devient vrai et GpuUndervolt/GpuPowerLimit.ApplyNvidiaAsync
    // joue ; sur une machine sans NVIDIA nvapi64.dll est absente → IsAvailable == false et le
    // chemin NVIDIA se dégrade proprement. Critique pour la sécurité : résoudre la couture et lire
    // IsAvailable ne doit pas lever ; ce même binaire part sur des machines sans carte NVIDIA. La
    // valeur d'IsAvailable ne se fige pas (la machine de développement peut avoir une carte NVIDIA),
    // donc on vérifie les vrais types concrets et que la lecture ne lève pas et rend un booléen lisible.
    [Fact]
    public void Resolves_REAL_nvapi_read_seam_not_stub()
    {
        using var provider = Build();

        provider.GetService<INvApiNative>().Should().BeOfType<NvApiNative>();

        var nvapi = provider.GetService<INvApiGpuTuning>();
        nvapi.Should().BeOfType<NvApiGpuTuningReader>(
            "N4 wires the real reader in place of the fail-clean StubNvApiGpuTuning");

        // Garantie sans plantage : sonder paresseusement nvapi64.dll (présente ou non) ne doit pas
        // lever ; elle se dit simplement disponible ou non. C'est la propriété qui rend la couture sûre
        // à livrer sur une machine sans NVIDIA. La valeur dépend du matériel, donc on ne la fige pas.
        bool? available = null;
        var read = () => available = nvapi!.IsAvailable;
        read.Should().NotThrow("the NVAPI boundary is fail-clean — no NVIDIA driver must never crash");
        available.Should().NotBeNull("IsAvailable resolved to a readable bool (true on NVIDIA, false otherwise)");
    }

    // L'INVERSE de l'ancien test. Il exigeait que l'App resolve tout le graphe
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

    // L'App câble le magasin d'état voulu en vérification seule (lire et montrer la dérive) et
    // un notificateur. Le signataire et la garde de la clé privée (chemin de commit) ne sont pas
    // câblés ici, donc cette surface ne peut que lire ; la résolution doit quand même réussir.
    [Fact]
    public void Resolves_verify_only_desired_state_store_and_notifier()
    {
        using var provider = Build();
        provider.GetService<IDesiredStateStore>().Should().NotBeNull();
        provider.GetService<IReconcilerNotifier>().Should().BeOfType<AppReconcilerNotifier>();
    }

}
