using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// -----------------------------------------------------------------------
// M2 Phase E3 — GpuPowerLimitTweak.
//
// Same invariants as E2: desktop dGPU gating, clamp via GpuSafeBounds (the power
// limit never goes below the safe floor — a starved GPU stutters, it doesn't gain),
// capture the original → serialized inverse op snapshot, apply via the allowlisted
// ApplyNvApiPowerLimitOp / ADLX, revert, fail-clean. HONESTY: efficiency / cooler,
// never "+FPS".
// -----------------------------------------------------------------------
public sealed class GpuPowerLimitTweakTests
{
    private readonly INvApiGpuTuning _nvApi = Substitute.For<INvApiGpuTuning>();
    private readonly IAdlxGpuTuning _adlx = Substitute.For<IAdlxGpuTuning>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private static readonly SettingsState State = new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On);

    private static HardwareInventory Hw(string gpuModel, string gpuVendor, long vram, ChassisType chassis) => new(
        new CpuInfo("Ryzen 7 7800X3D", "AMD", 8, 16, IsX3D: true, CcdCount: 1),
        new[] { new GpuInfo(gpuModel, gpuVendor, vram, "551.23", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1.2", null, true),
        chassis);

    private static HardwareInventory DesktopRtx4070() => Hw("NVIDIA GeForce RTX 4070", "NVIDIA", 12L << 30, ChassisType.Desktop);
    private static HardwareInventory DesktopAmd() => Hw("AMD Radeon RX 7800 XT", "AMD", 16L << 30, ChassisType.Desktop);
    private static HardwareInventory LaptopOptimus() => Hw("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", 8L << 30, ChassisType.Laptop);
    private static HardwareInventory HandheldDeck() => Hw("AMD Custom GPU 0405", "AMD", 0, ChassisType.Handheld);

    private GpuPowerLimitTweak Build(int requestedPct = 80, HardwareInventory? amdPresenceHw = null)
    {
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetPowerLimitPct(0).Returns(Result<int>.Ok(100)); // stock = 100%
        _adlx.IsAvailable.Returns(false);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(0)); // default: single tunable GPU at 0
        _adlx.GetPowerLimitPct(0).Returns(Result<int>.Ok(100));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
        // M6 — the tweak now reads its target from the live IGpuTuningTargetSource; seed it
        // with the test's power-limit target (the tuning default is unused by this tweak).
        var source = new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, requestedPct));
        // Presence is a real hardware fact computed by the source from the scanned inventory
        // (never forced on a fake seam) — set it from real AMD hw when asked.
        if (amdPresenceHw is not null)
            source.SetAmdGpuPresenceFromHardware(amdPresenceHw);
        return new GpuPowerLimitTweak(_nvApi, _adlx, _elevated, source);
    }

    [Fact]
    public void Has_expected_id_category_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("gpu.power_limit");
        t.Category.Should().Be(TweakCategory.GpuTuning);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
    }

    [Fact]
    public void SummaryKey_is_honest_no_fps_claim()
    {
        Build().SummaryKey.Should().Be("tweak.gpu.power_limit.efficiency");
        Build().SummaryKey.Should().NotContain("fps");
    }

    // --- Gating: desktop dGPU only. ---

    [Fact]
    public void AppliesTo_true_on_desktop_dgpu() =>
        Build().AppliesTo(DesktopRtx4070(), State).Should().BeTrue();

    [Fact]
    public void AppliesTo_false_on_laptop() =>
        Build().AppliesTo(LaptopOptimus(), State).Should().BeFalse();

    [Fact]
    public void AppliesTo_false_on_handheld() =>
        Build().AppliesTo(HandheldDeck(), State).Should().BeFalse();

    // --- Clamp: never below the floor. ---

    [Fact]
    public async Task Apply_clamps_below_floor_request_up_to_the_floor()
    {
        var t = Build(requestedPct: 30); // below MinPowerLimitPct

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // The applied value is raised to the safe floor — never the dangerous 30%.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiPowerLimitOp>(o => o.PowerLimitPercent == GpuSafeBounds.MinPowerLimitPct),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_applies_an_in_range_request_unchanged()
    {
        var t = Build(requestedPct: 80);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyNvApiPowerLimitOp>(o => o.GpuIndex == 0 && o.PowerLimitPercent == 80),
            Arg.Any<CancellationToken>());
    }

    // --- Capture original → snapshot. ---

    [Fact]
    public async Task Apply_captures_original_power_limit_in_snapshot()
    {
        var t = Build(requestedPct: 80);
        _nvApi.GetPowerLimitPct(0).Returns(Result<int>.Ok(95)); // some prior limit

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.IsOk.Should().BeTrue();
        inverse.Value.Should().BeOfType<ApplyNvApiPowerLimitOp>()
            .Which.PowerLimitPercent.Should().Be(95);
    }

    // --- AMD branch. ---

    [Fact]
    public async Task Apply_uses_adlx_power_limit_on_amd()
    {
        var t = Build(requestedPct: 80);
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.GetPowerLimitPct(0).Returns(Result<int>.Ok(100));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o => o.Kind == AdlxTuningKind.PowerLimit && o.IntValue == 80),
            Arg.Any<CancellationToken>());
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        inverse.Value.Should().BeOfType<ApplyAdlxTuningOp>()
            .Which.IntValue.Should().Be(100);
    }

    // --- AMD discrete-GPU selection (T4): resolve the tunable dGPU, not the iGPU. ---

    [Fact]
    public async Task Apply_amd_resolves_discrete_gpu_index_and_targets_it()
    {
        // iGPU at index 0 (not tunable) + discrete AMD dGPU at index 1. The resolver picks 1,
        // and the capture + apply both target index 1 — never the iGPU at 0.
        var t = Build(requestedPct: 80);
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.ResolveTunableGpuIndex().Returns(Result<int>.Ok(1));
        _adlx.GetPowerLimitPct(1).Returns(Result<int>.Ok(95));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        // The applied power-limit op targeted the discrete card at index 1.
        await _elevated.Received().ExecuteAsync(
            Arg.Is<ApplyAdlxTuningOp>(o =>
                o.GpuIndex == 1 && o.Kind == AdlxTuningKind.PowerLimit && o.IntValue == 80),
            Arg.Any<CancellationToken>());
        // The captured-original inverse op (for revert) carries index 1 and the read value.
        var inverse = PrivilegedOpCodec.Deserialize(r.Value!.OriginalValueJson);
        var op = inverse.Value.Should().BeOfType<ApplyAdlxTuningOp>().Subject;
        op.GpuIndex.Should().Be(1);
        op.IntValue.Should().Be(95);
    }

    [Fact]
    public async Task Apply_amd_fails_clean_when_no_tunable_gpu_resolved()
    {
        // 0-tunable-GPU box: the resolver fails → fail-clean, NO apply op, never Ok.
        var t = Build(requestedPct: 80);
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(true);
        _adlx.ResolveTunableGpuIndex()
            .Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "no tunable AMD GPU found"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // --- Fail-clean. ---

    [Fact]
    public async Task Apply_fails_clean_when_no_sdk_available()
    {
        var t = Build();
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(false);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_fails_clean_when_capture_fails()
    {
        var t = Build();
        _nvApi.GetPowerLimitPct(0).Returns(Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "no nvapi"));

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
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "boom")));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Apply_maps_thrown_seam_exception_to_failure()
    {
        var t = Build();
        _nvApi.GetPowerLimitPct(0).Returns(_ => throw new InvalidOperationException("kaboom"));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // --- Honesty: AMD GPU present but ADLX unavailable → explicit honest status. ---

    [Fact]
    public async Task Apply_returns_amd_tuning_unavailable_when_amd_gpu_present_and_adlx_unavailable()
    {
        // §22.3: when tuning cannot be done for this setup, SAY SO — never silently skip.
        // Presence comes from the real source computing it from real AMD hardware
        // (GpuGating.IsAmd) — never forced on a fake seam.
        var t = Build(amdPresenceHw: DesktopAmd());
        _nvApi.IsAvailable.Returns(false);
        _adlx.IsAvailable.Returns(false);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.GpuAmdTuningUnavailable);
        r.Error!.Value.Code.Should().NotBe(ErrorCodes.TweakSdkUnavailable,
            because: "must be distinct from a generic SDK unavailable");
        r.Error!.Value.Code.Should().NotBe(ErrorCodes.TweakNotApplicable,
            because: "must not look like the tweak does not apply to this hardware");
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // --- CAPABILITY PROBE (hardware-coverage doctrine). ---

    [Fact]
    public async Task Apply_refuses_when_the_identity_probe_write_does_not_take()
    {
        // Same doctrine as the undervolt: prove the power-limit write is live on this card
        // (identity write + read-back) before capping anything. Driver says ok, nothing moved
        // → refuse honestly, tune nothing.
        var t = Build(requestedPct: 80);
        _nvApi.GetPowerLimitPct(0).Returns(Result<int>.Ok(100), Result<int>.Ok(112));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnsupportedOnThisGpu);
        await _elevated.DidNotReceive().ExecuteAsync(
            Arg.Is<ApplyNvApiPowerLimitOp>(o => o.PowerLimitPercent == 80), Arg.Any<CancellationToken>());
    }

    // --- Revert. ---

    [Fact]
    public async Task Revert_restores_the_captured_original_via_helper()
    {
        var t = Build();
        var original = new ApplyNvApiPowerLimitOp(0, PowerLimitPercent: 100);
        var snap = new TweakSnapshot("gpu.power_limit", PrivilegedOpCodec.Serialize(original).Value!);

        var r = await t.RevertAsync(snap);

        r.IsOk.Should().BeTrue();
        await _elevated.Received(1).ExecuteAsync(
            Arg.Is<ApplyNvApiPowerLimitOp>(o => o.PowerLimitPercent == 100), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_malformed_snapshot_fail_clean()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("gpu.power_limit", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    // Revue n° 11 : la valeur inscrite pour la désinstallation est celle que la carte relit, pas celle demandée.
    [Fact]
    public async Task La_valeur_inscrite_est_celle_que_la_carte_relit()
    {
        var t = Build(requestedPct: 80);
        _nvApi.GetPowerLimitPct(0).Returns(Result<int>.Ok(100), Result<int>.Ok(100), Result<int>.Ok(79));

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        PrivilegedOpCodec.Deserialize(r.Value!.AppliedValueJson).Value.Should().Be(new ApplyNvApiPowerLimitOp(0, 79));
    }
}
