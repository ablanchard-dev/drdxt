using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// -----------------------------------------------------------------------
// NS-1 — GameDvrBackgroundOffTweak. Turns off Game DVR background recording via
// HKCU\...\GameDVR\HistoricalCaptureEnabled = 0 (surgical: manual capture untouched).
// Single-leaf, reboot-required (deferred A/B proof). NSubstitute fakes, no real registry.
// -----------------------------------------------------------------------
public sealed class GameDvrBackgroundOffTweakTests
{
    private const string GameDvrPath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string HistoricalCaptureName = "HistoricalCaptureEnabled";

    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static readonly HardwareInventory Hw = new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "560.94", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.A0", null, true),
        ChassisType.Desktop);

    private GameDvrBackgroundOffTweak Build()
    {
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName)
            .Returns(Result<uint?>.Ok(1u)); // currently on (1)
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        return new GameDvrBackgroundOffTweak(_registry, _elevated);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("latency.gameDvrBackgroundOff");
        t.Category.Should().Be(TweakCategory.Latency);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
        t.TakesEffectAfterReboot.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_is_honest_and_never_claims_fps()
    {
        Build().SummaryKey.Should().Be("tweak.latency.game_dvr.background_off");
        Build().SummaryKey.Should().NotContain("fps");
    }

    [Fact]
    public void AppliesTo_when_background_recording_is_on()
    {
        Build().AppliesTo(Hw, State).Should().BeTrue(); // lu = 1
    }

    [Fact]
    public void AppliesTo_is_false_when_already_off()
    {
        // Revue du 12/09 (C1) : une machine deja reglee ne doit plus porter ce levier au plan,
        // sinon « rien a tenter » n'est jamais vrai et le client passe par l'achat pour rien.
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName)
            .Returns(Result<uint?>.Ok(0u));

        t.AppliesTo(Hw, State).Should().BeFalse();
    }

    [Fact]
    public void AppliesTo_is_false_when_absent_because_the_windows_default_is_OFF()
    {
        // 14/09/2026 (C1 bis) : ce test affirmait « absent = defaut Windows (actif) ». Faux :
        // « Record what happened » est desactive par defaut (valeur absente = 0). C'est le cas du
        // poste de reference, et c'est lui qui tenait « rien a tenter » faux pour toujours.
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName)
            .Returns(Result<uint?>.Ok((uint?)null));

        t.AppliesTo(Hw, State).Should().BeFalse();
    }

    [Fact]
    public void AppliesTo_when_unreadable()
    {
        // Illisible = non lu, jamais « deja regle ».
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName)
            .Returns(Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "illisible"));

        t.AppliesTo(Hw, State).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_captures_original_and_writes_background_off_via_helper()
    {
        var t = Build(); // original = 1 (on)

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // Disables background recording: writes HistoricalCaptureEnabled = 0 on the leaf.
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o =>
                o.Hive == RegistryHiveKind.CurrentUser
                && o.Path == GameDvrPath
                && o.Name == HistoricalCaptureName
                && o.Kind == RegistryValueType.DWord
                && o.Value == "0"),
            Arg.Any<CancellationToken>());
        // Snapshot carries the captured original (1) as a serialized inverse op.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        inverse.Value.Should().BeOfType<SetRegistryValueOp>().Which.Value.Should().Be("1");
    }

    [Fact]
    public async Task Apply_when_value_absent_captures_a_delete_inverse()
    {
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName)
            .Returns(Result<uint?>.Ok((uint?)null));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value
            .Should().BeOfType<DeleteRegistryValueOp>();
    }

    [Fact]
    public async Task Apply_fails_clean_when_registry_read_fails()
    {
        var t = Build();
        _registry.ReadDword(RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName)
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
            RegistryHiveKind.CurrentUser, GameDvrPath, HistoricalCaptureName, RegistryValueType.DWord, "1");
        var snap = new TweakSnapshot("latency.gameDvrBackgroundOff", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Name == HistoricalCaptureName && o.Value == "1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("latency.gameDvrBackgroundOff", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
