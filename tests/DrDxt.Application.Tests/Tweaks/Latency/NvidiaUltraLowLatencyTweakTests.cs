using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// -----------------------------------------------------------------------
// NvidiaUltraLowLatencyTweak.
//
// Pose le plancher pilote NVIDIA « Low Latency Mode = Ultra » via la feuille de registre
// ...\NVTweak\LowLatencyMode en liste blanche (par IElevatedClient). Capture d'abord
// la vraie valeur d'origine (couture de lecture du registre) et la stocke en
// PrivilegedOp inverse sérialisée pour un retour arrière fidèle.
//
//  - AppliesTo : GPU de rendu NVIDIA sur un bureau. Faux sur AMD seul, iGPU, console portable.
//  - L'application capture l'origine → instantané, applique via le helper, échoue proprement
//    si le chemin SDK ou de lecture du registre est indisponible.
//  - Le retour arrière rend l'origine capturée.
//
// Tests unitaires purs avec des faux NSubstitute : aucun accès réel au registre ni au SDK.
// -----------------------------------------------------------------------
public sealed class NvidiaUltraLowLatencyTweakTests
{
    private const string NvTweakPath = @"SOFTWARE\NVIDIA Corporation\Global\NVTweak";
    private const string LowLatencyName = "LowLatencyMode";

    private readonly INvApi _nvApi = Substitute.For<INvApi>();
    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static HardwareInventory Hw(string gpuModel, string gpuVendor, ChassisType chassis) => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo(gpuModel, gpuVendor, 12L << 30, "551.23", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1.2", null, true),
        chassis);

    private static HardwareInventory DesktopRtx4070() => Hw("NVIDIA GeForce RTX 4070", "NVIDIA", ChassisType.Desktop);
    private static HardwareInventory AmdRadeonOnly() => Hw("AMD Radeon RX 7800 XT", "AMD", ChassisType.Desktop);
    private static HardwareInventory HandheldDeck() => Hw("AMD Custom GPU 0405", "AMD", ChassisType.Handheld);

    private static HardwareInventory IntelIgpuOnly() => new(
        new CpuInfo("Core i5-13600", "Intel", 14, 20, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("Intel UHD Graphics 770", "Intel", 0, "31.0", TriState.Unknown) },
        new[] { new MemoryModule("PN", "Corsair", 5600, 5600) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    private static HardwareInventory LaptopNvidia() => Hw("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", ChassisType.Laptop);

    private NvidiaUltraLowLatencyTweak Build()
    {
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetLowLatencyMode().Returns(Result<uint>.Ok(0)); // désactivé à l'origine
        _registry.ReadDword(RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName)
            .Returns(Result<uint?>.Ok(0u));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        return new NvidiaUltraLowLatencyTweak(_nvApi, _registry, _elevated);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("latency.reflex");
        t.Category.Should().Be(TweakCategory.Latency);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_is_the_honest_driver_floor_key()
    {
        // Honnêteté : le vrai Reflex par jeu est dans le jeu ; ceci n'est que le plancher pilote.
        Build().SummaryKey.Should().Be("tweak.latency.reflex.driver_floor");
        Build().SummaryKey.Should().NotContain("fps");
    }

    // La valeur écrite n'est lue par aucun pilote connu (absente de NvApiDriverSettings.h
    // et du registre de la machine de référence). Le cas le plus favorable ne la propose plus.
    [Fact]
    public void Jamais_propose_meme_sur_un_bureau_nvidia() =>
        Build().AppliesTo(DesktopRtx4070(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_amd_only() =>
        Build().AppliesTo(AmdRadeonOnly(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_igpu_only() =>
        Build().AppliesTo(IntelIgpuOnly(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_handheld() =>
        Build().AppliesTo(HandheldDeck(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_laptop_locked_nvidia() =>
        Build().AppliesTo(LaptopNvidia(), State).Should().BeFalse();

    // NVIDIA 436.02 : sans effet en DX12. L'exe du jeu est un vrai fichier
    // Windows dont la table d'import a ete relevee : Taskmgr.exe = d3d12 seul, GamePanel.exe = d3d11.
    private NvidiaUltraLowLatencyTweak AvecJeu(string? exe)
    {
        Build();
        var jeu = Substitute.For<IGameExecutable>();
        jeu.RunningGameExecutablePath().Returns(exe);
        return new NvidiaUltraLowLatencyTweak(_nvApi, _registry, _elevated, jeu);
    }

    private static string Sys(string nom) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), nom);

    [Fact]
    [Trait("Category", "Machine")] // lit la table d'import d'un vrai executable Windows
    public void Not_offered_while_a_DirectX12_only_game_runs() =>
        AvecJeu(Sys("Taskmgr.exe")).AppliesTo(DesktopRtx4070(), State).Should().BeFalse();

    [Fact]
    [Trait("Category", "Machine")]
    public void Jamais_propose_meme_avec_un_jeu_DirectX11() =>
        AvecJeu(Sys("GamePanel.exe")).AppliesTo(DesktopRtx4070(), State).Should().BeFalse();

    // La regle etait « jeu inconnu : on ne retire rien ». Sur la machine de reference, un
    // scan fait jeu ferme recommandait donc ce levier alors que le jeu joue (cod.exe) est DX12 seul, ou
    // il ne peut rien. Son effet depend ENTIEREMENT de l'API du jeu : sans jeu lu, il n'y a rien a
    // affirmer, donc rien a recommander.
    [Fact]
    public void Not_offered_when_the_game_is_unknown() =>
        AvecJeu(null).AppliesTo(DesktopRtx4070(), State).Should().BeFalse();

    [Fact]
    public async Task Apply_captures_original_and_writes_ultra_via_helper()
    {
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName)
            .Returns(Result<uint?>.Ok(1u)); // actuellement « On » (1), pas Ultra

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // Le plancher pilote : écrit LowLatencyMode = 2 (Ultra) sur la feuille en liste blanche.
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o =>
                o.Hive == RegistryHiveKind.LocalMachine
                && o.Path == NvTweakPath
                && o.Name == LowLatencyName
                && o.Kind == RegistryValueType.DWord
                && o.Value == "2"),
            Arg.Any<CancellationToken>());
        // L'instantané porte l'origine capturée (1) en op inverse sérialisée.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        inverse.Value.Should().BeOfType<SetRegistryValueOp>()
            .Which.Value.Should().Be("1");
    }

    [Fact]
    public async Task Apply_when_value_was_absent_captures_a_delete_inverse()
    {
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName)
            .Returns(Result<uint?>.Ok((uint?)null)); // valeur absente à l'origine

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // Retour fidèle d'une valeur ajoutée = la supprimer.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.Value.Should().BeOfType<DeleteRegistryValueOp>();
    }

    [Fact]
    public async Task Apply_fails_clean_when_nvapi_unavailable()
    {
        var t = Build();
        _nvApi.IsAvailable.Returns(false);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_fails_clean_when_registry_read_fails()
    {
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName)
            .Returns(Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "registry unreadable"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_surfaces_helper_failure_without_throwing()
    {
        var t = Build();
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "write failed")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Apply_maps_thrown_seam_exception_to_failure()
    {
        var t = Build();
        _registry.ReadDword(Arg.Any<RegistryHiveKind>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public async Task Revert_restores_the_captured_original_via_helper()
    {
        var t = Build();
        var original = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName, RegistryValueType.DWord, "0");
        var snap = new TweakSnapshot("latency.reflex", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Name == LowLatencyName && o.Value == "0"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("latency.reflex", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
