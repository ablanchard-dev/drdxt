using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// -----------------------------------------------------------------------
// GameModeOnTweak. Active le Mode Jeu de Windows en écrivant les deux feuilles GameBar
// (AllowAutoGameMode + AutoGameModeEnabled) = 1 via un instantané composite ; redémarrage
// requis (preuve A/B différée). Tests unitaires purs avec des faux NSubstitute.
// -----------------------------------------------------------------------
public sealed class GameModeOnTweakTests
{
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string AllowName = "AllowAutoGameMode";
    private const string EnabledName = "AutoGameModeEnabled";

    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static SettingsState State(TriState gameMode) => new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: gameMode, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static readonly HardwareInventory Hw = new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "560.94", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.A0", null, true),
        ChassisType.Desktop);

    private GameModeOnTweak Build()
    {
        // Origines : AllowAutoGameMode = 0 (désactivé), AutoGameModeEnabled absent.
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, AllowName)
            .Returns(Result<uint?>.Ok(0u));
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, EnabledName)
            .Returns(Result<uint?>.Ok((uint?)null));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        return new GameModeOnTweak(_registry, _elevated);
    }

    [Fact]
    public void An_UNREADABLE_game_mode_is_not_a_deficit()
    {
        // Le test qui mord. `SettingsState.GameMode` etait un bool, et le lecteur ecrasait
        // TriState.Unknown en false : sur une machine ou le reglage ne se lit pas, DrDXT
        // concluait « Game Mode desactive » et proposait de le reparer — un deficit FABRIQUE
        // a partir d'une lecture ratee. On ne repare pas ce qu'on n'a pas su lire.
        Build().AppliesTo(Hw, State(TriState.Unknown)).Should().BeFalse();

        // Et les deux cas lisibles restent francs.
        Build().AppliesTo(Hw, State(TriState.Off)).Should().BeTrue();
        Build().AppliesTo(Hw, State(TriState.On)).Should().BeFalse();
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("latency.gameModeOn");
        t.Category.Should().Be(TweakCategory.Latency);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
        t.TakesEffectAfterReboot.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_is_honest_and_never_claims_fps()
    {
        Build().SummaryKey.Should().Be("tweak.latency.game_mode.enable");
        Build().SummaryKey.Should().NotContain("fps");
    }

    [Fact]
    public void AppliesTo_true_only_when_game_mode_is_off()
    {
        var t = Build();
        t.AppliesTo(Hw, State(gameMode: TriState.Off)).Should().BeTrue();
        t.AppliesTo(Hw, State(gameMode: TriState.On)).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_writes_both_gamebar_leaves_on_via_helper()
    {
        var t = Build();

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Hive == RegistryHiveKind.CurrentUser
                && o.Path == GameBarPath && o.Name == AllowName && o.Value == "1"),
            Arg.Any<CancellationToken>());
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Hive == RegistryHiveKind.CurrentUser
                && o.Path == GameBarPath && o.Name == EnabledName && o.Value == "1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_snapshot_is_a_composite_restoring_both_originals()
    {
        var t = Build(); // Allow = 0, Enabled = absent

        var r = await t.ApplyAsync();

        var ops = CompositeSnapshotCodec.Decode(r.Value!.OriginalValueJson);
        ops.IsOk.Should().BeTrue();
        ops.Value.Should().HaveCount(2);
        // AllowAutoGameMode d'origine 0 → rendu à « 0 ».
        ops.Value.OfType<SetRegistryValueOp>()
            .Should().ContainSingle(s => s.Name == AllowName && s.Value == "0");
        // AutoGameModeEnabled était absent → le retour fidèle le supprime.
        ops.Value.OfType<DeleteRegistryValueOp>()
            .Should().ContainSingle(d => d.Name == EnabledName);
    }

    [Fact]
    public async Task Apply_fails_clean_when_a_registry_read_fails()
    {
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, EnabledName)
            .Returns(Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "unreadable"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_rolls_back_the_first_leaf_when_the_second_write_fails()
    {
        var t = Build();
        // La première écriture (Allow) réussit ; la seconde (Enabled) échoue → la feuille Allow doit être annulée.
        _elevated.ExecuteAsync(
                Arg.Is<SetRegistryValueOp>(o => o.Name == EnabledName && o.Value == "1"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "write failed")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        // Le rollback a rendu AllowAutoGameMode à son « 0 » d'origine.
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Name == AllowName && o.Value == "0"),
            Arg.Any<CancellationToken>());
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
    public async Task Revert_replays_the_composite_originals_via_helper()
    {
        var t = Build();
        var ops = new List<PrivilegedOp>
        {
            new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, AllowName, RegistryValueType.DWord, "0"),
            new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, EnabledName),
        };
        var snap = new TweakSnapshot("latency.gameModeOn", CompositeSnapshotCodec.Encode(ops).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Name == AllowName && o.Value == "0"), Arg.Any<CancellationToken>());
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<DeleteRegistryValueOp>(o => o.Name == EnabledName), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("latency.gameModeOn", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
