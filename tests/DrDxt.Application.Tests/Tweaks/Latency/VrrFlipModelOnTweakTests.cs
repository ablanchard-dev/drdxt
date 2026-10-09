using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Latency;

// -----------------------------------------------------------------------
// NS-1 — VrrFlipModelOnTweak. Turns on VRR + flip-model by MERGING the shared
// DirectXUserGlobalSettings REG_SZ (preserving AutoHDR et al.). The merge logic is
// exercised here through the public tweak surface (the written string is asserted).
// Reboot-required (deferred A/B proof). NSubstitute fakes, no real registry.
// -----------------------------------------------------------------------
public sealed class VrrFlipModelOnTweakTests
{
    private const string PrefsPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string ValueName = "DirectXUserGlobalSettings";

    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.Off);

    private static readonly HardwareInventory Hw = new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "560.94", TriState.On) },
        new[] { new MemoryModule("PN", "Corsair", 6000, 6000) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.A0", null, true),
        ChassisType.Desktop);

    private VrrFlipModelOnTweak Build(string? original)
    {
        _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName)
            .Returns(Result<string?>.Ok(original));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        return new VrrFlipModelOnTweak(_registry, _elevated);
    }

    private async Task<string> CapturedWrittenValue(VrrFlipModelOnTweak t)
    {
        var r = await t.ApplyAsync();
        r.IsOk.Should().BeTrue();
        // Inspect the recorded calls: the single SetRegistryValueOp on our value carries the merge.
        var write = _elevated.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<SetRegistryValueOp>()
            .Single(o => o.Name == ValueName);
        return write.Value;
    }

    // 06/10/2026 (plan 4.3) : sans la valeur posée, la désinstallation ne sait pas si la chaîne est encore celle de
    // DrDXT, et n'y touche pas (« valeur posée inconnue »). Ses voisins HKCU la portaient déjà (SnapshotAvec).
    [Fact]
    public async Task Snapshot_carries_the_merged_string_it_wrote()
    {
        var t = Build("SwapEffectUpgradeEnable=1;");
        var r = await t.ApplyAsync();

        var pose = PrivilegedOpCodec.Deserialize(r.Value!.AppliedValueJson!).Value.Should()
            .BeOfType<SetRegistryValueOp>().Subject;
        pose.Value.Should().Be(_elevated.ReceivedCalls().Select(c => c.GetArguments()[0])
            .OfType<SetRegistryValueOp>().Single().Value);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build("");
        t.Id.Should().Be("latency.vrrFlipModelOn");
        t.Category.Should().Be(TweakCategory.Latency);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
        t.TakesEffectAfterReboot.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_is_honest_and_never_claims_fps()
    {
        Build("").SummaryKey.Should().Be("tweak.latency.vrr_flipmodel.enable");
        Build("").SummaryKey.Should().NotContain("fps");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("VRROptimizeEnable=1")]
    [InlineData("AutoHDREnable=1;VRROptimizeEnable=1;SwapEffectUpgradeEnable=0")]
    public void AppliesTo_while_flip_model_is_not_on_whatever_the_display(string? original)
    {
        Build(original).AppliesTo(Hw, State).Should().BeTrue();
    }

    // 14/09/2026 (C1 bis) : flip-model deja la, reste la seule moitie VRR. Elle ne vaut rien sur un
    // ecran qui ne fait pas de VRR : le poste de reference (`SwapEffectUpgradeEnable=1;`) gardait ce
    // levier au plan pour rien, et « rien a tenter » n'etait jamais vrai.

    [Theory]
    [InlineData("SwapEffectUpgradeEnable=1;")]
    [InlineData("AutoHDREnable=1;VRROptimizeEnable=0;SwapEffectUpgradeEnable=1")]
    public void AppliesTo_for_the_VRR_half_only_on_a_display_READ_as_VRR_capable(string original)
    {
        Build(original).AppliesTo(Hw, State with { VrrCapableDisplay = TriState.On }).Should().BeTrue();
    }

    [Theory]
    [InlineData(TriState.Off)]
    [InlineData(TriState.Unknown)]
    public void AppliesTo_is_false_for_the_VRR_half_alone_when_the_display_is_not_read_as_VRR(TriState ecran)
    {
        Build("SwapEffectUpgradeEnable=1;").AppliesTo(Hw, State with { VrrCapableDisplay = ecran })
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("VRROptimizeEnable=1;SwapEffectUpgradeEnable=1")]
    [InlineData("AutoHDREnable=1; swapeffectupgradeenable=1 ;VRROptimizeEnable=1;")]
    public void AppliesTo_is_false_when_both_toggles_are_already_on(string original)
    {
        // Revue du 12/09 (C1) : deja regle ⇒ hors du plan, sinon le paiement est demande pour rien.
        Build(original).AppliesTo(Hw, State).Should().BeFalse();
    }

    [Fact]
    public void AppliesTo_when_unreadable()
    {
        var t = Build("");
        _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName)
            .Returns(Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "illisible"));
        t.AppliesTo(Hw, State).Should().BeTrue(); // non lu n'est pas « deja regle »
    }

    [Fact]
    public async Task Apply_merges_and_preserves_autohdr_and_flips_existing_vrr()
    {
        // VRR off (0) → flipped to 1; AutoHDR preserved; flip-model appended.
        var written = await CapturedWrittenValue(Build("AutoHDREnable=1;VRROptimizeEnable=0"));
        written.Should().Be("AutoHDREnable=1;VRROptimizeEnable=1;SwapEffectUpgradeEnable=1");
    }

    [Fact]
    public async Task Apply_on_absent_value_writes_just_the_two_keys()
    {
        var written = await CapturedWrittenValue(Build(original: null));
        written.Should().Be("VRROptimizeEnable=1;SwapEffectUpgradeEnable=1");
    }

    [Fact]
    public async Task Apply_preserves_a_value_that_contains_special_chars()
    {
        // HighPerfAdapter holds a "&"-separated id and must survive the merge verbatim.
        var written = await CapturedWrittenValue(Build("HighPerfAdapter=10DE&2684&89321043;AutoHDREnable=1"));
        written.Should().Be(
            "HighPerfAdapter=10DE&2684&89321043;AutoHDREnable=1;VRROptimizeEnable=1;SwapEffectUpgradeEnable=1");
    }

    [Fact]
    public async Task Apply_snapshot_restores_the_raw_original_string()
    {
        var r = await Build("AutoHDREnable=1;VRROptimizeEnable=0").ApplyAsync();

        r.IsOk.Should().BeTrue();
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        inverse.Value.Should().BeOfType<SetRegistryValueOp>()
            .Which.Value.Should().Be("AutoHDREnable=1;VRROptimizeEnable=0"); // exact original
    }

    [Fact]
    public async Task Apply_snapshot_is_a_delete_when_value_was_absent()
    {
        var r = await Build(original: null).ApplyAsync();

        r.IsOk.Should().BeTrue();
        PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson).Value
            .Should().BeOfType<DeleteRegistryValueOp>();
    }

    [Fact]
    public async Task Apply_fails_clean_when_registry_read_fails()
    {
        var t = Build("");
        _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName)
            .Returns(Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "unreadable"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_surfaces_helper_failure_without_throwing()
    {
        var t = Build("");
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "write failed")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Apply_maps_thrown_seam_exception_to_failure()
    {
        var t = Build("");
        _registry.ReadString(Arg.Any<RegistryHiveKind>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    [Fact]
    public async Task Revert_restores_the_original_string_via_helper()
    {
        var t = Build("");
        var original = new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, PrefsPath, ValueName, RegistryValueType.String, "AutoHDREnable=1");
        var snap = new TweakSnapshot("latency.vrrFlipModelOn", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Name == ValueName && o.Value == "AutoHDREnable=1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build("");

        var r = await t.RevertAsync(new TweakSnapshot("latency.vrrFlipModelOn", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
}
